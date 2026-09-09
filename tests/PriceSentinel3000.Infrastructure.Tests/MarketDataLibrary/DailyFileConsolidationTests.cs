using System.Globalization;
using System.Security.Cryptography;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed partial class DailyFileConsolidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PriceSentinel-daily-merge-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
    private string Root => Path.Combine(_directory, "library");
    private JsonMarketDataLibrary Library => new(Root);

    [Fact]
    public void Consolidate_UnionsLegacyFragmentsAndSnapshotsPreservingGapsAndExactPins()
    {
        HistoricalCandle[] expected = new[] { -1, 0, 1, 3, 4 }.Select(Bar).ToArray();
        var sources = new List<(HistoricalDatasetInfo Info, byte[] Bytes)>();
        sources.AddRange(Seed(Download(Bar(0), Bar(1), Bar(3)) with
        {
            RequestedThroughUtc = Start.AddSeconds(75),
        }));
        sources.AddRange(Seed(Download(Bar(-1), Bar(0)) with { SessionBounds = "24_5" }));
        sources.AddRange(Seed(Download(Bar(3), Bar(4)) with { FetchedAtUtc = Start.AddDays(2) }));
        Assert.Equal(3, Library.Scan().Datasets.Count);

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();
        HistoricalDatasetInfo merged = Assert.Single(scan.Datasets);
        HistoricalDataset dataset = Library.Read(merged.DatasetHash);

        Assert.Empty(scan.Diagnostics);
        Assert.Equal(expected, dataset.Candles);
        Assert.Equal("24_5", dataset.SessionBounds);
        Assert.Equal(Start.AddDays(2), dataset.FetchedAtUtc);
        Assert.Equal(Start.AddSeconds(-15), dataset.Coverage.RequestedFromUtc);
        Assert.Equal(Start.AddSeconds(75), dataset.Coverage.RequestedThroughUtc);
        Assert.Equal(6, dataset.Coverage.ExpectedCandleCount);
        Assert.Equal(5, dataset.Coverage.ActualCandleCount);
        Assert.False(dataset.Coverage.Complete);
        Assert.Equal(new HistoricalGap(Start.AddSeconds(30), Start.AddSeconds(45)), Assert.Single(dataset.Coverage.Gaps));
        Assert.Equal("2026/09 - September/NFLX/2026-09-04.15s.json", merged.RelativePath.Replace('\\', '/'));
        Assert.Equal(Path.Combine(Root, merged.RelativePath), Assert.Single(ActiveJsonFiles()));
        foreach ((HistoricalDatasetInfo original, byte[] bytes) in sources)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Root, ".archive", original.DatasetHash + ".json")));
            HistoricalDataset archived = Library.Read(original.DatasetHash);
            Assert.Equal(original.DatasetHash, archived.DatasetHash);
            HistoricalDataQueryResult pinned = Library.Query(new("NFLX", original.Coverage.RequestedFromUtc,
                original.Coverage.RequestedThroughUtc, PinnedHashes: [original.DatasetHash]));
            Assert.True(pinned.Succeeded);
            Assert.Equal(original.DatasetHash, Assert.Single(pinned.Datasets).DatasetHash);
            Assert.Equal(archived.Candles, pinned.Candles);
        }

        Dictionary<string, string> snapshot = JsonSnapshot();
        Assert.Equal(merged.DatasetHash, Assert.Single(Library.ConsolidateDailyFiles().Datasets).DatasetHash);
        Assert.Equal(merged.DatasetHash, Assert.Single(Library.ConsolidateDailyFiles().Datasets).DatasetHash);
        AssertSnapshotUnchanged(snapshot);
    }

    [Fact]
    public void MergedDailyFile_IsPortableWithoutItsArchive()
    {
        Seed(Download(Bar(0), Bar(1)));
        Seed(Download(Bar(1), Bar(2)));
        HistoricalDatasetInfo merged = Assert.Single(Library.ConsolidateDailyFiles().Datasets);
        string portableRoot = Path.Combine(_directory, "portable");
        Directory.CreateDirectory(portableRoot);
        File.Copy(Path.Combine(Root, merged.RelativePath), Path.Combine(portableRoot, "copied-day.json"));
        var portable = new JsonMarketDataLibrary(portableRoot);

        Assert.Empty(portable.Scan().Diagnostics);
        Assert.Equal(merged.DatasetHash, Assert.Single(portable.Scan().Datasets).DatasetHash);
        HistoricalDataQueryResult result = portable.Query(new("NFLX", Start, Start.AddSeconds(45), PinnedHashes: [merged.DatasetHash]));
        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(new[] { Bar(0), Bar(1), Bar(2) }, result.Candles);
        Assert.False(Directory.Exists(Path.Combine(portableRoot, ".archive")));
    }

    [Fact]
    public void Save_AppendsAndFillsMissingCandlesWithoutReplacingEarlierCoverage()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(Bar(0), Bar(2))));
        Library.Save(Download(Bar(2), Bar(3)) with { SessionBounds = "extended" });
        HistoricalDatasetInfo filled = Assert.Single(Library.Save(Download(Bar(1))));
        HistoricalDataset merged = Library.Read(filled.DatasetHash);

        Assert.Equal(new[] { Bar(0), Bar(1), Bar(2), Bar(3) }, merged.Candles);
        Assert.True(merged.Coverage.Complete);
        Assert.Empty(merged.Coverage.Gaps);
        Assert.Equal("extended", merged.SessionBounds);
        Assert.Equal(new[] { Bar(0), Bar(2) }, Library.Read(original.DatasetHash).Candles);
        Assert.Equal(Path.Combine(Root, filled.RelativePath), Assert.Single(ActiveJsonFiles()));
        Dictionary<string, string> snapshot = JsonSnapshot();
        HistoricalDatasetInfo duplicate = Assert.Single(Library.Save(Download(Bar(1), Bar(2)) with { FetchedAtUtc = Start.AddDays(5) }));
        Assert.Equal(filled.DatasetHash, duplicate.DatasetHash);
        AssertSnapshotUnchanged(snapshot);
    }

    [Theory]
    [InlineData("2026-09-05T03:59:45Z", "2026-09-05T04:00:15Z", 2)]
    [InlineData("2026-10-01T03:59:45Z", "2026-10-01T04:00:15Z", 2)]
    [InlineData("2027-01-01T04:59:45Z", "2027-01-01T05:00:15Z", 2)]
    [InlineData("2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z", 1)]
    [InlineData("2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z", 1)]
    public void Consolidate_PreservesEveryBoundaryCandleAndActualDstDayLength(string fromText, string throughText, int days)
    {
        DateTimeOffset from = DateTimeOffset.Parse(fromText, CultureInfo.InvariantCulture);
        DateTimeOffset through = DateTimeOffset.Parse(throughText, CultureInfo.InvariantCulture);
        int count = checked((int)((through - from).TotalSeconds / 15));
        HistoricalCandle[] expected = Enumerable.Range(0, count).Select(index => Candle(from.AddSeconds(index * 15), 80m + index)).ToArray();
        int middle = count / 2;
        Seed(Download(expected[..(middle + 1)]) with { SessionBounds = "24_5" });
        Seed(Download(expected[middle..]) with { SessionBounds = "24_5" });

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();
        Assert.Empty(scan.Diagnostics);
        Assert.Equal(days, scan.Datasets.Count);
        Assert.Equal(days, ActiveJsonFiles().Length);
        Assert.Equal(days, scan.Datasets.Select(item => item.TradingDate).Distinct().Count());
        Assert.All(scan.Datasets, item =>
        {
            Assert.EndsWith(item.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".15s.json", item.RelativePath);
            Assert.True(item.Coverage.Complete);
        });
        HistoricalDataQueryResult result = Library.Query(new("NFLX", from, through));
        Assert.True(result.Succeeded);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(count, result.Coverage.ExpectedCandleCount);
        Assert.Equal(expected, result.Candles);
        Assert.Equal(from, result.Candles[0].StartsAtUtc);
        Assert.Equal(through, result.Candles[^1].EndsAtUtc);
    }

    [Fact]
    public async Task ConcurrentCollectors_MergeDisjointAndOverlappingChunksWithoutLostUpdates()
    {
        HistoricalCandle[] expected = Enumerable.Range(0, 18).Select(Bar).ToArray();
        HistoricalDownload[] downloads = Enumerable.Range(0, 8)
            .Select(index => Download(expected.Skip(index * 2).Take(4).ToArray())).ToArray();

        IReadOnlyList<HistoricalDatasetInfo>[] saved = await Task.WhenAll(downloads
            .Select(download => Task.Run(() => new JsonMarketDataLibrary(Root).Save(download))));

        MarketDataLibraryScan scan = Library.Scan();
        Assert.Empty(scan.Diagnostics);
        HistoricalDatasetInfo merged = Assert.Single(scan.Datasets);
        Assert.Equal(expected, Library.Read(merged.DatasetHash).Candles);
        Assert.True(merged.Coverage.Complete);
        Assert.Single(ActiveJsonFiles());
        for (int index = 0; index < saved.Length; index++)
        {
            HistoricalDataset snapshot = Library.Read(Assert.Single(saved[index]).DatasetHash);
            Assert.All(downloads[index].Candles, candle => Assert.Contains(candle, snapshot.Candles));
        }
    }

    [Theory]
    [InlineData("price")]
    [InlineData("volume")]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("adjustmentPolicy")]
    [InlineData("adjustmentBasis")]
    [InlineData("shifted")]
    public void Save_RejectsConflictsWithoutChangingExistingFiles(string conflict)
    {
        Library.Save(Download(Bar(0)));
        Dictionary<string, string> before = JsonSnapshot();

        Assert.Throws<InvalidDataException>(() => Library.Save(Conflicting(Download(Bar(0)), conflict)));

        AssertSnapshotUnchanged(before);
        Assert.Equal(Bar(0), Assert.Single(Library.Read(Assert.Single(Library.Scan().Datasets).DatasetHash).Candles));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("volume")]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("adjustmentPolicy")]
    [InlineData("adjustmentBasis")]
    public void Consolidate_IncompatibleLegacyFilesRemainIntactAndDiagnosed(string conflict)
    {
        Seed(Download(Bar(0)));
        Seed(Conflicting(Download(Bar(0)), conflict));
        Dictionary<string, string> before = JsonSnapshot();

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();

        Assert.Equal(2, scan.Datasets.Count);
        Assert.Contains(scan.Diagnostics, item => item.Code == "daily_merge_blocked");
        AssertSnapshotUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consolidate_CorruptOrOccupiedCanonicalDestinationPreservesLegacySources(bool occupiedByDirectory)
    {
        (HistoricalDatasetInfo original, byte[] bytes) = Assert.Single(Seed(Download(Bar(0))));
        string canonical = Path.Combine(Root, Path.GetDirectoryName(original.RelativePath)!, "2026-09-04.15s.json");
        if (occupiedByDirectory) Directory.CreateDirectory(canonical);
        else File.WriteAllText(canonical, "broken original");

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();

        Assert.Contains(scan.Diagnostics, item => item.Code == "daily_merge_blocked");
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Root, original.RelativePath)));
        Assert.Equal(Bar(0), Assert.Single(Library.Read(original.DatasetHash).Candles));
        if (occupiedByDirectory) Assert.True(Directory.Exists(canonical));
        else Assert.Equal("broken original", File.ReadAllText(canonical));
    }

    [Fact]
    public void Consolidate_LeavesCoarseSourceRevisionsUnchanged()
    {
        HistoricalDownload first = Download(Candle(Start, 80m, 60)) with { SourceIntervalSeconds = 60 };
        Library.Save(first);
        Library.Save(Download(Candle(Start.AddSeconds(60), 81m, 60)) with { SourceIntervalSeconds = 60 });
        Dictionary<string, string> before = JsonSnapshot();

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();

        Assert.Equal(2, scan.Datasets.Count);
        Assert.All(scan.Datasets, item => Assert.Equal(60, item.SourceIntervalSeconds));
        AssertSnapshotUnchanged(before);
    }

    [Fact]
    public void Consolidate_RemovesPhysicalDuplicateEvenWhenScanDeduplicatesItsHash()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(Bar(0), Bar(1))));
        string canonical = Path.Combine(Root, original.RelativePath);
        string duplicate = Path.Combine(Path.GetDirectoryName(canonical)!, "2026-09-04.15s.rev-" + original.DatasetHash + ".json");
        File.Copy(canonical, duplicate);
        Assert.Single(Library.Scan().Datasets);
        Assert.Equal(2, ActiveJsonFiles().Length);

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();

        Assert.Empty(scan.Diagnostics);
        Assert.Equal(original.DatasetHash, Assert.Single(scan.Datasets).DatasetHash);
        Assert.Equal(canonical, Assert.Single(ActiveJsonFiles()));
        Assert.Equal(new[] { Bar(0), Bar(1) }, Library.Read(original.DatasetHash).Candles);
    }

    [Fact]
    public void Consolidate_CaseOnlyCanonicalPathDifferenceNeverDeletesTheActiveFile()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(Bar(0))));
        string directory = Path.GetDirectoryName(Path.Combine(Root, original.RelativePath))!;
        string lowerDirectory = Path.Combine(Path.GetDirectoryName(directory)!, "nflx");
        Directory.Move(directory, directory + "-case");
        Directory.Move(directory + "-case", lowerDirectory);
        string lowerFile = Path.Combine(lowerDirectory, "2026-09-04.15s.json");
        string caseFile = Path.Combine(lowerDirectory, "2026-09-04.15S.JSON");
        File.Move(lowerFile, lowerFile + ".case");
        File.Move(lowerFile + ".case", caseFile);

        MarketDataLibraryScan scan = Library.ConsolidateDailyFiles();

        Assert.Empty(scan.Diagnostics);
        Assert.Equal(original.DatasetHash, Assert.Single(scan.Datasets).DatasetHash);
        Assert.Single(ActiveJsonFiles());
        Assert.Equal(Bar(0), Assert.Single(Library.Read(original.DatasetHash).Candles));
        Assert.True(File.Exists(Path.Combine(Root, Assert.Single(scan.Datasets).RelativePath)));
    }

    [Fact]
    public void Save_LaterDayConflictLeavesEarlierDayAndItsHistoryUnchanged()
    {
        DateTimeOffset at = new(2026, 9, 5, 3, 59, 30, TimeSpan.Zero);
        HistoricalCandle first = Candle(at, 80m);
        HistoricalCandle secondDay = Candle(at.AddSeconds(30), 81m);
        IReadOnlyList<HistoricalDatasetInfo> originals = Library.Save(Download(first, secondDay));
        Assert.Equal(2, originals.Count);
        Dictionary<string, string> before = JsonSnapshot();
        HistoricalDownload incoming = Download(Candle(at.AddSeconds(15), 82m), Candle(at.AddSeconds(30), 90m));

        Assert.Throws<InvalidDataException>(() => Library.Save(incoming));

        AssertSnapshotUnchanged(before);
        Assert.Equal(first, Assert.Single(Library.Read(originals[0].DatasetHash).Candles));
        Assert.Equal(secondDay, Assert.Single(Library.Read(originals[1].DatasetHash).Candles));
    }
    private IReadOnlyList<(HistoricalDatasetInfo Info, byte[] Bytes)> Seed(HistoricalDownload download)
    {
        string sourceRoot = Path.Combine(_directory, "sources", Guid.NewGuid().ToString("N"));
        var source = new JsonMarketDataLibrary(sourceRoot);
        var copied = new List<(HistoricalDatasetInfo, byte[])>();
        foreach (HistoricalDatasetInfo info in source.Save(download))
        {
            string relative = Path.Combine(Path.GetDirectoryName(info.RelativePath)!,
                info.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".15s.rev-" + info.DatasetHash + ".json");
            string destination = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            byte[] bytes = File.ReadAllBytes(Path.Combine(sourceRoot, info.RelativePath));
            File.WriteAllBytes(destination, bytes);
            copied.Add((info with { RelativePath = relative }, bytes));
        }
        return copied;
    }

    private string[] ActiveJsonFiles() => Directory.GetFiles(Root, "*.json", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(Root, path).StartsWith(".archive" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToArray();

    private Dictionary<string, string> JsonSnapshot() => Directory.GetFiles(Root, "*.json", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private void AssertSnapshotUnchanged(Dictionary<string, string> before)
    {
        Dictionary<string, string> after = JsonSnapshot();
        Assert.Equal(before.Count, after.Count);
        foreach ((string path, string hash) in before)
        {
            Assert.True(after.TryGetValue(path, out string? actual), $"Original file disappeared: {path}");
            Assert.Equal(hash, actual);
        }
    }

    private static HistoricalDownload Conflicting(HistoricalDownload download, string conflict) => conflict switch
    {
        "price" => download with { Candles = [Candle(Start, 81m)] },
        "volume" => download with { Candles = [Bar(0) with { Volume = null }] },
        "provider" => download with { Provider = "OtherProvider" },
        "instrument" => download with { InstrumentId = "other-instrument" },
        "adjustmentPolicy" => download with { AdjustmentPolicy = "unadjusted" },
        "adjustmentBasis" => download with { AdjustmentBasis = "different-split-basis" },
        "shifted" => Download(Candle(Start.AddSeconds(5), 80m)),
        _ => throw new ArgumentOutOfRangeException(nameof(conflict)),
    };

    private static HistoricalDownload Download(params HistoricalCandle[] candles) => new("Robinhood", "instrument-nflx", "NFLX", 15,
        "split", "robinhood-split-unversioned", "regular", candles.Max(item => item.EndsAtUtc).AddDays(1),
        candles.Min(item => item.StartsAtUtc), candles.Max(item => item.EndsAtUtc), candles);

    private static HistoricalCandle Bar(int index) => Candle(Start.AddSeconds(index * 15), 80m + index);
    private static HistoricalCandle Candle(DateTimeOffset at, decimal price, int seconds = 15) =>
        new(at, at.AddSeconds(seconds), at.AddSeconds(seconds), price, price + 1m, price - 1m, price, 123.456m);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
