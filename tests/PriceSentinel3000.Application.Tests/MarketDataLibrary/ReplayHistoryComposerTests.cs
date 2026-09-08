using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests.MarketDataLibrary;

public sealed class ReplayHistoryComposerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-08T14:00:00Z");

    [Fact]
    public void SameIntervalGapsAreCombinedAndLocalValuesWinOverProviderDuplicates()
    {
        ReplayHistorySource local = Source(15, [Bar(0), Bar(30)]);
        ReplayHistorySource provider = Source(15,
            [Bar(0, open: 20, high: 22, low: 19, close: 21), Bar(15), Bar(45)], pending: true);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 60), [local, provider]);

        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(4, result.Candles.Count);
        Assert.Equal(local.Candles[0], result.Candles[0]);
        Assert.Equal(new[] { 0d, 15d, 30d, 45d }, result.Candles.Select(c => (c.StartsAtUtc - Start).TotalSeconds));
        Assert.Equal(new[] { local, provider }, result.Sources);
        Assert.Equal(local.Datasets[0].DatasetHash, result.Sources[0].Datasets[0].DatasetHash);
    }

    [Fact]
    public void IntactCoarseBarReplacesOverlappingIncompleteFinePartitionExactlyOnce()
    {
        ReplayHistorySource fine = Source(15, [Bar(0), Bar(30), Bar(45), Bar(60), Bar(75), Bar(90), Bar(105)]);
        HistoricalCandle coarseFirst = Bar(0, 60, open: 20, high: 25, low: 18, close: 23, volume: 240);
        ReplayHistorySource coarse = Source(60, [coarseFirst, Bar(60, 60, volume: 900)], pending: true);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 120), [fine, coarse]);

        Assert.Equal(60, result.SourceIntervalSeconds);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(coarseFirst, result.Candles[0]);
        Assert.Equal(400m, result.Candles[1].Volume);
        Assert.Equal(result.Candles[0].EndsAtUtc, result.Candles[1].StartsAtUtc);
        Assert.Equal(new[] { fine, coarse }, result.Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregatedOhlcvUsesChronologicalPricesAndOnlyKnownVolumes(bool unknownVolume)
    {
        ReplayHistorySource fine = Source(15,
        [
            Bar(0, open: 10, high: 14, low: 8, close: 12, volume: 1),
            Bar(15, open: 12, high: 17, low: 11, close: 16, volume: unknownVolume ? null : 2),
            Bar(30, open: 16, high: 18, low: 13, close: 14, volume: 3),
            Bar(45, open: 14, high: 20, low: 7, close: 19, volume: 4),
        ]);
        ReplayHistorySource coarse = Source(60, [Bar(60, 60)]);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 120), [fine, coarse]);

        HistoricalCandle aggregated = result.Candles[0];
        Assert.Equal(60, result.SourceIntervalSeconds);
        Assert.Equal(10m, aggregated.Open);
        Assert.Equal(20m, aggregated.High);
        Assert.Equal(7m, aggregated.Low);
        Assert.Equal(19m, aggregated.Close);
        Assert.Equal(unknownVolume ? null : (decimal?)10, aggregated.Volume);
        Assert.Equal(aggregated.EndsAtUtc, aggregated.AvailableAtUtc);
        Assert.Equal(!unknownVolume, result.Coverage.HasCompleteVolume);
    }

    [Fact]
    public void IncompleteBucketsDoNotGeneratePartialOrSyntheticCandles()
    {
        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 60),
            [Source(15, [Bar(0)]), Source(30, [Bar(30, 30)])]);

        Assert.Equal(30, result.SourceIntervalSeconds);
        Assert.Equal(Bar(30, 30), Assert.Single(result.Candles));
        Assert.Equal(new HistoricalGap(Start, Start.AddSeconds(30)), Assert.Single(result.Coverage.Gaps));
        Assert.False(result.Coverage.Complete);
        Assert.Equal(2, result.Coverage.ExpectedCandleCount);
        Assert.Equal(1, result.Coverage.ActualCandleCount);
    }

    [Fact]
    public void EqualPartialCoveragePrefersFinestIntervalAndReportsOnlyUsedSources()
    {
        ReplayHistorySource fine = Source(15, [Bar(0), Bar(15)]);
        ReplayHistorySource coarse = Source(30, [Bar(0, 30)]);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 60), [fine, coarse]);

        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Equal(2, result.Candles.Count);
        Assert.Same(fine, Assert.Single(result.Sources));
    }

    [Fact]
    public void MoreCoveredDurationWinsWhenNoCandidateIsComplete()
    {
        ReplayHistorySource fine = Source(15, [Bar(0)]);
        ReplayHistorySource coarse = Source(60, [Bar(60, 60)]);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 180), [fine, coarse]);

        Assert.Equal(60, result.SourceIntervalSeconds);
        Assert.Equal(Bar(60, 60), Assert.Single(result.Candles));
        Assert.Same(coarse, Assert.Single(result.Sources));
        Assert.Equal(2, result.Coverage.Gaps.Count);
    }

    [Theory]
    [InlineData(7, 68, 3, false)]
    [InlineData(15, 75, 4, true)]
    public void RangeEdgesRetainOnlyWholeAlignedBuckets(int from, int through, int count, bool complete)
    {
        ReplayHistorySource source = Source(15, Enumerable.Range(0, 6).Select(i => Bar(i * 15)).ToArray());
        HistoricalDataQuery query = Query(from, through);

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(query, [source]);

        Assert.Equal(count, result.Candles.Count);
        Assert.Equal(complete, result.Coverage.Complete);
        Assert.All(result.Candles, candle =>
        {
            Assert.True(candle.StartsAtUtc >= query.FromUtc);
            Assert.True(candle.EndsAtUtc <= query.ThroughUtc);
            Assert.Equal(0, candle.StartsAtUtc.UtcTicks % TimeSpan.FromSeconds(15).Ticks);
        });
        if (!complete)
        {
            Assert.Equal(new[] { new HistoricalGap(Start.AddSeconds(7), Start.AddSeconds(15)),
                new HistoricalGap(Start.AddSeconds(60), Start.AddSeconds(68)) }, result.Coverage.Gaps);
            Assert.Equal(5, result.Coverage.ExpectedCandleCount);
        }
    }

    [Fact]
    public void CoarseBarCrossingRequestedEdgeIsNeverSplit()
    {
        HistoricalDataQuery query = Query(15, 135);
        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(query, [Source(120, [Bar(0, 120)])]);

        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Empty(result.Candles);
        Assert.Empty(result.Sources);
        Assert.Equal(new HistoricalGap(query.FromUtc, query.ThroughUtc), Assert.Single(result.Coverage.Gaps));
    }

    [Fact]
    public void NoSourcesReturnsExplicitEmptyFifteenSecondCoverage()
    {
        HistoricalDataQuery query = Query(0, 120);
        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(query, []);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Empty(result.Candles);
        Assert.Empty(result.Sources);
        Assert.Equal(8, result.Coverage.ExpectedCandleCount);
        Assert.Equal(new HistoricalGap(query.FromUtc, query.ThroughUtc), Assert.Single(result.Coverage.Gaps));
        Assert.False(result.Coverage.Complete);
        Assert.False(result.Coverage.HasCompleteVolume);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("symbol")]
    [InlineData("policy")]
    [InlineData("basis")]
    [InlineData("session")]
    public void DifferentContributorProvenanceIsRejectedEvenForPartialCandidates(string field)
    {
        ReplayHistorySource first = Source(15, [Bar(0)]);
        ReplayHistorySource other = Source(15, [Bar(15)], pending: true);
        HistoricalDownload download = other.PendingDownload!;
        other = other with { PendingDownload = field switch
        {
            "provider" => download with { Provider = "other" },
            "instrument" => download with { InstrumentId = "other" },
            "symbol" => download with { Symbol = "NVDA" },
            "policy" => download with { AdjustmentPolicy = "none" },
            "basis" => download with { AdjustmentBasis = "other" },
            _ => download with { SessionBounds = "extended" },
        } };

        Assert.Throws<InvalidDataException>(() => ReplayHistoryComposer.Compose(Query(0, 45), [first, other]));
    }

    [Fact]
    public void MismatchedCoarseContributorCannotSilentlyJoinFineHistory()
    {
        ReplayHistorySource fine = Source(15, [Bar(0), Bar(15)]);
        ReplayHistorySource coarse = Source(30, [Bar(30, 30)], pending: true);
        coarse = coarse with { PendingDownload = coarse.PendingDownload! with { InstrumentId = "other" } };

        Assert.Throws<InvalidDataException>(() => ReplayHistoryComposer.Compose(Query(0, 90), [fine, coarse]));
    }

    [Fact]
    public void CompleteFineHistoryReturnsBeforeUnusedConflictingSourceProvenance()
    {
        ReplayHistorySource fine = Source(15, [Bar(0), Bar(15), Bar(30), Bar(45)]);
        ReplayHistorySource coarse = Source(60, [Bar(0, 60)], pending: true);
        coarse = coarse with { PendingDownload = coarse.PendingDownload! with { InstrumentId = "other" } };

        ReplayHistoryComposition result = ReplayHistoryComposer.Compose(Query(0, 60), [fine, coarse]);

        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.True(result.Coverage.Complete);
        Assert.Same(fine, Assert.Single(result.Sources));
    }

    private static HistoricalDataQuery Query(int from, int through) =>
        new("SOFI", Start.AddSeconds(from), Start.AddSeconds(through), SessionBounds: "regular");

    private static HistoricalCandle Bar(int offset, int seconds = 15, decimal open = 10, decimal high = 12,
        decimal low = 9, decimal close = 11, decimal? volume = 100) =>
        new(Start.AddSeconds(offset), Start.AddSeconds(offset + seconds), Start.AddSeconds(offset + seconds),
            open, high, low, close, volume);

    private static ReplayHistorySource Source(int interval, HistoricalCandle[] candles, bool pending = false)
    {
        DateTimeOffset from = candles.Length == 0 ? Start : candles.Min(c => c.StartsAtUtc);
        DateTimeOffset through = candles.Length == 0 ? Start.AddMinutes(2) : candles.Max(c => c.EndsAtUtc);
        var download = new HistoricalDownload("test", "SOFI-id", "SOFI", interval, "split", "basis", "regular",
            Start.AddHours(1), from, through, candles);
        if (pending) return new(interval, [], candles, download);
        var info = new HistoricalDatasetInfo(interval.ToString("x").PadLeft(64, '0'), "test.json", "test",
            "SOFI-id", "SOFI", new(2026, 9, 8), interval, "split", "basis", "regular", download.FetchedAtUtc,
            new(from, through, from, through, (int)((through - from).TotalSeconds / interval),
                candles.Length, true, candles.All(c => c.Volume.HasValue), []));
        return new(interval, [info], candles);
    }
}
