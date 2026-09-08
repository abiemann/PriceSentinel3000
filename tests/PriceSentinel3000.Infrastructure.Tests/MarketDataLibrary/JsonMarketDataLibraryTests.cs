using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class JsonMarketDataLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PriceSentinel-library-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
    private JsonMarketDataLibrary Library => new(_directory);

    [Fact]
    public void SaveRead_PreservesExactPricesUnknownVolumeAndSourceCloseAvailability()
    {
        HistoricalDownload download = Download() with
        {
            Candles = [Candle(Start, 80.12345678901234567890123456m) with { Volume = null }],
        };
        HistoricalDatasetInfo info = Assert.Single(Library.Save(download));
        HistoricalDataset dataset = Library.Read(info.DatasetHash);
        HistoricalCandle candle = Assert.Single(dataset.Candles);

        Assert.Equal(download.Candles[0], candle);
        Assert.Null(candle.Volume);
        Assert.True(info.Coverage.Complete);
        Assert.False(info.Coverage.HasCompleteVolume);
        Assert.Equal("America/New_York", dataset.GroupingTimeZone);
        Assert.Equal("2026/09 - September/NFLX/2026-09-04.15s.json", info.RelativePath.Replace('\\', '/'));
        Assert.Equal(64, info.DatasetHash.Length);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, info.RelativePath)));
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("candles")[0].GetProperty("open").ValueKind);
        Assert.Equal("80.12345678901234567890123456", document.RootElement.GetProperty("candles")[0].GetProperty("open").GetString());
        Assert.True(File.Exists(Path.Combine(_directory, "README.md")));
    }

    [Fact]
    public void Save_DeduplicatesIdenticalLaterFetchWithoutChangingPinnedFile()
    {
        HistoricalDownload download = Download();
        HistoricalDatasetInfo first = Assert.Single(Library.Save(download));
        byte[] original = File.ReadAllBytes(Path.Combine(_directory, first.RelativePath));

        HistoricalDatasetInfo second = Assert.Single(Library.Save(download with { FetchedAtUtc = download.FetchedAtUtc.AddDays(1) }));

        Assert.Equal(first.DatasetHash, second.DatasetHash);
        Assert.Equal(first.FetchedAtUtc, second.FetchedAtUtc);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_directory, first.RelativePath)));
        Assert.Single(Library.Scan().Datasets);
    }

    [Fact]
    public async Task ConcurrentCollectors_DeduplicateIdenticalDataWithoutOverwriting()
    {
        HistoricalDownload download = Download();
        IReadOnlyList<HistoricalDatasetInfo>[] results = await Task.WhenAll(
            Task.Run(() => Library.Save(download)), Task.Run(() => Library.Save(download)));

        Assert.Equal(results[0][0].DatasetHash, results[1][0].DatasetHash);
        Assert.Single(Library.Scan().Datasets);
        Assert.DoesNotContain(Library.Scan().Diagnostics, item => item.Code == "interrupted_write");
    }

    [Fact]
    public async Task ConcurrentCorrections_PreserveBothImmutableVersions()
    {
        HistoricalDownload download = Download();
        await Task.WhenAll(Task.Run(() => Library.Save(download)),
            Task.Run(() => Library.Save(download with { Candles = [Candle(Start, 81m)] })));

        Assert.Equal(2, Library.Scan().Datasets.Count);
        Assert.Equal(new[] { 80m, 81m }, Library.Scan().Datasets.Select(info => Library.Read(info.DatasetHash).Candles[0].Open).Order());
    }

    [Fact]
    public void CorruptCanonicalDailyFile_IsPreservedWhileValidRevisionIsSaved()
    {
        string relative = "2026/09 - September/NFLX/2026-09-04.15s.json";
        string path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "broken original");

        HistoricalDatasetInfo saved = Assert.Single(Library.Save(Download()));

        Assert.Contains(".rev-", saved.RelativePath);
        Assert.Equal("broken original", File.ReadAllText(path));
        Assert.Equal(80m, Library.Read(saved.DatasetHash).Candles[0].Open);
        Assert.Contains(Library.Scan().Diagnostics, item => item.Code == "invalid_dataset");
    }

    [Fact]
    public void CorrectedData_PreservesRevisionsAndRequiresExplicitSelection()
    {
        HistoricalDownload original = Download();
        HistoricalDatasetInfo first = Assert.Single(Library.Save(original));
        HistoricalDatasetInfo corrected = Assert.Single(Library.Save(original with
        {
            FetchedAtUtc = original.FetchedAtUtc.AddDays(1), Candles = [Candle(Start, 81m)],
        }));

        Assert.Contains($".rev-{corrected.DatasetHash}.json", corrected.RelativePath);
        Assert.Equal(80m, Assert.Single(Library.Read(first.DatasetHash).Candles).Open);
        Assert.Equal(2, Library.Scan().Datasets.Count);
        Assert.Contains(Library.Scan().Diagnostics, item => item.Code == "conflicting_revisions");
        Assert.False(Library.Query(Query()).Succeeded);
        Assert.False(Library.Query(Query() with { RevisionPolicy = HistoricalRevisionPolicy.CompatibleCoverage }).Succeeded);
        HistoricalDataQueryResult newest = Library.Query(Query() with { RevisionPolicy = HistoricalRevisionPolicy.LatestFetched });
        Assert.True(newest.Succeeded);
        Assert.Equal(corrected.DatasetHash, Assert.Single(newest.Datasets).DatasetHash);
        HistoricalDataQueryResult pinned = Library.Query(Query() with { PinnedHashes = [first.DatasetHash] });
        Assert.Equal(80m, Assert.Single(pinned.Candles).Open);
    }

    [Fact]
    public void CopiedDayFile_IsSelfContainedAndRescannedWithoutSidecars()
    {
        HistoricalDatasetInfo source = Assert.Single(Library.Save(Download()));
        string otherRoot = Path.Combine(_directory, "portable");
        Directory.CreateDirectory(otherRoot);
        File.Copy(Path.Combine(_directory, source.RelativePath), Path.Combine(otherRoot, "copied-day.json"));
        var copied = new JsonMarketDataLibrary(otherRoot);

        Assert.Empty(copied.Scan().Diagnostics);
        Assert.Equal(source.DatasetHash, Assert.Single(copied.Scan().Datasets).DatasetHash);
        HistoricalDataQueryResult result = copied.Query(Query() with { PinnedHashes = [source.DatasetHash] });
        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(80m, Assert.Single(result.Candles).Open);
    }

    [Fact]
    public void OvernightRange_SplitsByEasternStartDateAcrossMonthAndYear()
    {
        DateTimeOffset at = new(2027, 1, 1, 4, 59, 45, TimeSpan.Zero);
        HistoricalDownload download = Download() with
        {
            RequestedFromUtc = at, RequestedThroughUtc = at.AddSeconds(30), FetchedAtUtc = at.AddHours(1),
            Candles = [Candle(at, 80m), Candle(at.AddSeconds(15), 81m)],
        };
        CultureInfo previous = CultureInfo.CurrentCulture;
        IReadOnlyList<HistoricalDatasetInfo> saved;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            saved = Library.Save(download);
        }
        finally { CultureInfo.CurrentCulture = previous; }

        Assert.Equal(2, saved.Count);
        Assert.StartsWith("2026/12 - December/NFLX/2026-12-31", saved[0].RelativePath.Replace('\\', '/'));
        Assert.StartsWith("2027/01 - January/NFLX/2027-01-01", saved[1].RelativePath.Replace('\\', '/'));
        HistoricalDataQueryResult result = Library.Query(new("NFLX", at, at.AddSeconds(30)));
        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(2, result.Datasets.Count);
        Assert.False(Directory.Exists(Path.Combine(_directory, "2026", "11 - November")));
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void DailyFilename_UsesGregorianDateIndependentOfCurrentCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            HistoricalDatasetInfo saved = Assert.Single(Library.Save(Download()));
            Assert.Equal("2026/09 - September/NFLX/2026-09-04.15s.json", saved.RelativePath.Replace('\\', '/'));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Coverage_ReportsMissingEdgesAndInteriorWithoutInventingBars()
    {
        HistoricalDownload download = Download() with
        {
            RequestedThroughUtc = Start.AddSeconds(75),
            Candles = [Candle(Start.AddSeconds(15), 80m), Candle(Start.AddSeconds(45), 81m)],
        };
        HistoricalDatasetInfo info = Assert.Single(Library.Save(download));

        Assert.False(info.Coverage.Complete);
        Assert.Equal(5, info.Coverage.ExpectedCandleCount);
        Assert.Equal(2, info.Coverage.ActualCandleCount);
        Assert.Equal(new[] { new HistoricalGap(Start, Start.AddSeconds(15)),
            new HistoricalGap(Start.AddSeconds(30), Start.AddSeconds(45)),
            new HistoricalGap(Start.AddSeconds(60), Start.AddSeconds(75)) }, info.Coverage.Gaps);
        HistoricalDataQueryResult result = Library.Query(Query() with { ThroughUtc = Start.AddSeconds(75) });
        Assert.Equal(2, result.Candles.Count);
        Assert.False(result.Coverage.Complete);
    }

    [Fact]
    public void Coverage_CompleteRegularWindowDoesNotClaimExtendedHours()
    {
        Library.Save(Download());
        Assert.True(Library.Query(Query()).Coverage.Complete);
        HistoricalDataQueryResult extended = Library.Query(Query() with { FromUtc = Start.AddHours(-1) });
        Assert.False(extended.Coverage.Complete);
        Assert.Single(extended.Coverage.Gaps);
    }

    [Theory]
    [InlineData("candles")]
    [InlineData("fetchedAtUtc")]
    [InlineData("coverage")]
    public void TamperedFile_IsDiagnosedAndPinnedReadNeverSubstitutes(string field)
    {
        HistoricalDownload original = Download();
        HistoricalDatasetInfo pinned = Assert.Single(Library.Save(original));
        Library.Save(original with { Candles = [Candle(Start, 81m)], FetchedAtUtc = original.FetchedAtUtc.AddDays(1) });
        string path = Path.Combine(_directory, pinned.RelativePath);
        JsonNode data = JsonNode.Parse(File.ReadAllText(path))!;
        if (field == "candles") data["candles"]![0]!["open"] = "80.1";
        if (field == "fetchedAtUtc") data[field] = original.FetchedAtUtc.AddHours(1).ToString("O");
        if (field == "coverage") data[field]!["actualCandleCount"] = 99;
        File.WriteAllText(path, data.ToJsonString());

        Assert.Contains(Library.Scan().Diagnostics, item => item.Code == "invalid_dataset");
        Assert.Throws<InvalidDataException>(() => Library.Read(pinned.DatasetHash));
        Assert.False(Library.Query(Query() with { PinnedHashes = [pinned.DatasetHash],
            RevisionPolicy = HistoricalRevisionPolicy.LatestFetched }).Succeeded);
    }

    [Fact]
    public void CorruptAndInterruptedFiles_AreDiagnosedAndNeverReplaced()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "corrupt.json"), "{ interrupted");
        File.WriteAllText(Path.Combine(_directory, "unfinished.json.tmp-test"), "{}");
        MarketDataLibraryScan scan = Library.Scan();

        Assert.Empty(scan.Datasets);
        Assert.Contains(scan.Diagnostics, item => item.Code == "invalid_dataset");
        Assert.Contains(scan.Diagnostics, item => item.Code == "interrupted_write");
        Assert.Single(Library.Save(Download()));
        Assert.Equal("{ interrupted", File.ReadAllText(Path.Combine(_directory, "corrupt.json")));
        Assert.DoesNotContain(Directory.EnumerateFiles(_directory, "*.tmp-*", SearchOption.AllDirectories),
            path => !path.EndsWith("unfinished.json.tmp-test", StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedFile_IsRejectedBeforeDeserialization()
    {
        Directory.CreateDirectory(_directory);
        using (var stream = File.Create(Path.Combine(_directory, "too-large.json"))) stream.SetLength(8 * 1024 * 1024 + 1);
        Assert.Contains(Library.Scan().Diagnostics, item => item.Code == "invalid_dataset" && item.Message.Contains("8 MiB"));
    }

    [Fact]
    public void LargeLibrary_DoesNotStopCollectingOrReadingAfterTenThousandFiles()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download()));
        string otherFiles = Path.Combine(_directory, "notes");
        Directory.CreateDirectory(otherFiles);
        for (int i = 0; i < 10_001; i++)
            File.WriteAllText(Path.Combine(otherFiles, $"note-{i}.txt"), "");

        Assert.Empty(Library.Scan().Diagnostics);
        Assert.Equal(original.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.True(Library.Query(Query()).Coverage.Complete);
        Assert.Equal(original.DatasetHash, Library.Read(original.DatasetHash).DatasetHash);
        Assert.Single(Library.Save(Download() with { Symbol = "SOXL", InstrumentId = "instrument-soxl" }));
        Assert.Equal(2, Library.Scan().Datasets.Count);
    }

    [Fact]
    public void CopiedDuplicateFiles_AreValidatedAndReturnedOnce()
    {
        HistoricalDatasetInfo saved = Assert.Single(Library.Save(Download()));
        string source = Path.Combine(_directory, saved.RelativePath);
        for (int i = 0; i < 20; i++)
            File.Copy(source, Path.Combine(_directory, $"copied-{i}.json"));

        MarketDataLibraryScan scan = Library.Scan();

        Assert.Empty(scan.Diagnostics);
        Assert.Equal(saved.DatasetHash, Assert.Single(scan.Datasets).DatasetHash);
        Assert.True(Library.Query(Query()).Coverage.Complete);
    }

    [Fact]
    public void AggregateFileSize_DoesNotImposeALifetimeLibraryLimit()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download()));
        for (int i = 0; i < 33; i++)
        {
            using var stream = File.Create(Path.Combine(_directory, $"invalid-{i}.json"));
            // These files meet the individual size limit but fail JSON validation
            // immediately. Their total exceeds the former 256 MiB library cap.
            stream.SetLength(8 * 1024 * 1024);
        }

        MarketDataLibraryScan scan = Library.Scan();

        Assert.Equal(33, scan.Diagnostics.Count);
        Assert.All(scan.Diagnostics, item => Assert.Equal("invalid_dataset", item.Code));
        Assert.Equal(original.DatasetHash, Assert.Single(scan.Datasets).DatasetHash);
        Assert.True(Library.Query(Query()).Coverage.Complete);
        Assert.Single(Library.Save(Download() with { Symbol = "SOXL", InstrumentId = "instrument-soxl" }));
    }

    [Fact]
    public void Query_DoesNotMergeProvidersOrAdjustmentBases()
    {
        HistoricalDownload download = Download();
        Library.Save(download);
        Library.Save(download with { Provider = "Other", AdjustmentBasis = "another-split-vintage" });

        Assert.False(Library.Query(Query() with { RevisionPolicy = HistoricalRevisionPolicy.LatestFetched }).Succeeded);
        Assert.True(Library.Query(Query() with { Provider = "Robinhood", AdjustmentBasis = "split" }).Succeeded);
    }

    [Theory]
    [InlineData("../NFLX")]
    [InlineData("NFLX/OTHER")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("NFLX.")]
    public void UnsafeSymbols_AreRejectedWithoutWriting(string symbol)
    {
        Assert.Throws<InvalidDataException>(() => Library.Save(Download() with { Symbol = symbol }));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void UnsupportedAvailabilityOrOhlcOrVolume_IsRejected()
    {
        HistoricalCandle original = Candle(Start, 80m);
        foreach (HistoricalCandle invalid in new[]
                 {
                     original with { AvailableAtUtc = original.EndsAtUtc.AddSeconds(1) },
                     original with { High = 1m }, original with { Low = 0m },
                     original with { Volume = -1m }, original with { EndsAtUtc = original.EndsAtUtc.AddSeconds(1) },
                 })
            Assert.Throws<InvalidDataException>(() => Library.Save(Download() with { Candles = [invalid] }));
        Assert.Throws<InvalidDataException>(() => Library.Read("../data.json"));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void CoarseSourceInterval_RemainsExplicitAndCannotSatisfyFinerQuery(int seconds)
    {
        HistoricalDownload download = Download() with
        {
            SourceIntervalSeconds = seconds, RequestedThroughUtc = Start.AddSeconds(seconds),
            Candles = [Candle(Start, 80m, seconds)],
        };
        HistoricalDatasetInfo info = Assert.Single(Library.Save(download));
        Assert.EndsWith($".{seconds}s.json", info.RelativePath);
        Assert.Empty(Library.Query(Query()).Candles);
        Assert.True(Library.Query(Query() with { SourceIntervalSeconds = seconds, ThroughUtc = Start.AddSeconds(seconds) }).Coverage.Complete);
    }

    private static HistoricalDownload Download() => new("Robinhood", "instrument-nflx", "NFLX", 15,
        "split", "split", "regular", Start.AddDays(1), Start, Start.AddSeconds(15), [Candle(Start, 80m)]);
    private static HistoricalDataQuery Query() => new("NFLX", Start, Start.AddSeconds(15));
    private static HistoricalCandle Candle(DateTimeOffset at, decimal price, int seconds = 15) =>
        new(at, at.AddSeconds(seconds), at.AddSeconds(seconds), price, price + 1m, price - 1m, price, 123.456m);
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
