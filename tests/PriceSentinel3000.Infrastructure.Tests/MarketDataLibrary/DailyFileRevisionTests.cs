using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed partial class DailyFileConsolidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_RevisionsReplacePricesAndLowerVolumesEvenWithoutNewCoverage(bool sameFetchTime)
    {
        HistoricalDownload download = Download(Bar(0), Bar(1));
        HistoricalDatasetInfo original = Assert.Single(Library.Save(download));
        HistoricalCandle correction = Candle(Start, 81m) with { Volume = 100m };
        HistoricalDatasetInfo current = Assert.Single(Library.Save(download with
        {
            Candles = [correction],
            FetchedAtUtc = sameFetchTime ? download.FetchedAtUtc : download.FetchedAtUtc.AddDays(1),
        }));

        Assert.Equal(new[] { correction, Bar(1) }, Library.Read(current.DatasetHash).Candles);
        Assert.Equal(new[] { Bar(0), Bar(1) }, Library.Read(original.DatasetHash).Candles);
        Assert.NotEqual(original.DatasetHash, current.DatasetHash);
        Assert.Single(ActiveJsonFiles());
    }

    [Theory]
    [InlineData("zero-prices")]
    [InlineData("zero-volume")]
    [InlineData("null-volume")]
    [InlineData("empty")]
    [InlineData("null-list")]
    [InlineData("null-entry")]
    public void Save_EmptyUpdatesKeepSavedValuesAndHash(string empty)
    {
        HistoricalDownload download = Download(Bar(0));
        HistoricalDatasetInfo original = Assert.Single(Library.Save(download));
        Dictionary<string, string> before = JsonSnapshot();
        HistoricalCandle bar = Bar(0);
        HistoricalDownload update = download with
        {
            FetchedAtUtc = download.FetchedAtUtc.AddDays(1),
            Candles = empty switch
            {
                "zero-prices" => [bar with { Open = 0, High = 0, Low = 0, Close = 0 }],
                "zero-volume" => [bar with { Volume = 0 }],
                "null-volume" => [bar with { Volume = null }],
                "empty" => [],
                "null-list" => null!,
                "null-entry" => [null!],
                _ => throw new ArgumentOutOfRangeException(nameof(empty)),
            },
        };

        HistoricalDatasetInfo current = Assert.Single(Library.Save(update));

        Assert.Equal(original.DatasetHash, current.DatasetHash);
        Assert.Equal(bar, Assert.Single(Library.Read(current.DatasetHash).Candles));
        AssertSnapshotUnchanged(before);
    }

    [Fact]
    public void Save_EmptyFieldsKeepSavedValuesWhileAcceptingOtherPositiveFields()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(Bar(0))));
        HistoricalCandle revised = Bar(0) with { Open = 0, High = 82m, Low = 0, Close = 81m, Volume = 0 };

        HistoricalDatasetInfo current = Assert.Single(Library.Save(Download(revised)));

        Assert.Equal(Bar(0) with { High = 82m, Close = 81m }, Assert.Single(Library.Read(current.DatasetHash).Candles));
        Assert.Equal(Bar(0), Assert.Single(Library.Read(original.DatasetHash).Candles));
    }

    [Fact]
    public void Save_PlaceholderWithoutSavedPriceIsOmittedWhileValidNewCandlesAreSaved()
    {
        HistoricalCandle placeholder = Bar(0) with { Low = 0 };

        HistoricalDatasetInfo current = Assert.Single(Library.Save(Download(placeholder, Bar(1))));

        Assert.Equal(Bar(1), Assert.Single(Library.Read(current.DatasetHash).Candles));
        Assert.False(current.Coverage.Complete);
        Assert.Equal(new HistoricalGap(Start, Start.AddSeconds(15)), Assert.Single(current.Coverage.Gaps));
    }

    [Fact]
    public void Save_ZeroPriceDoesNotHideDuplicateOrMalformedTiming()
    {
        HistoricalCandle placeholder = Bar(0) with { Open = 0, High = 0, Low = 0, Close = 0 };
        foreach (HistoricalDownload invalid in new[]
        {
            Download(placeholder, placeholder),
            Download(placeholder with { StartsAtUtc = Start.AddSeconds(1) }),
            Download(placeholder with { AvailableAtUtc = placeholder.EndsAtUtc.AddSeconds(1) }),
            Download(placeholder with { Volume = -1 }),
        })
            Assert.Throws<InvalidDataException>(() => Library.Save(invalid));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void Save_OlderSourceAddsMissingCandlesWithoutReplacingNewerSavedValues()
    {
        HistoricalCandle newer = Candle(Start, 82m);
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download(newer) with { FetchedAtUtc = Start.AddDays(3) }));

        HistoricalDatasetInfo current = Assert.Single(Library.Save(Download(Bar(0), Bar(1))));

        Assert.Equal(new[] { newer, Bar(1) }, Library.Read(current.DatasetHash).Candles);
        Assert.Equal(newer, Assert.Single(Library.Read(original.DatasetHash).Candles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consolidate_UsesNewestRevisionRegardlessOfFileOrderAndPreservesOldHashes(bool reverse)
    {
        HistoricalCandle newer = Candle(Start, 81m) with { Volume = 100m };
        HistoricalDownload[] downloads =
        [
            Download(Bar(0)) with { FetchedAtUtc = Start.AddDays(2) },
            Download(newer) with { FetchedAtUtc = Start.AddDays(3) },
        ];
        var original = (reverse ? downloads.Reverse() : downloads).SelectMany(Seed).ToArray();

        MarketDataLibraryScan result = Library.ConsolidateDailyFiles();

        Assert.Empty(result.Diagnostics);
        Assert.Equal(newer, Assert.Single(Library.Read(Assert.Single(result.Datasets).DatasetHash).Candles));
        foreach (var source in original)
            Assert.Equal(source.Bytes, File.ReadAllBytes(Path.Combine(Root, ".archive", source.Info.DatasetHash + ".json")));
    }

    [Fact]
    public void Save_UpdatesPreviouslyGeneratedReadmeWithRevisionRule()
    {
        Library.Save(Download(Bar(0)));
        string path = Path.Combine(Root, "README.md");
        File.WriteAllText(path, "Overlaps count once; conflicting candles or provenance are never overwritten. Replacement is atomic.");

        Library.Save(Download(Bar(0)));

        string readme = File.ReadAllText(path);
        Assert.Contains("newer valid prices and positive volumes replace saved values", readme);
        Assert.Contains("zero or missing fields keep saved values", readme);
        Assert.Contains("Incompatible provenance never merges", readme);
    }
}
