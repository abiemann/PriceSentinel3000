using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.Application.Tests.Strategies;

public sealed class ScriptBarSeriesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SeedCutoffExcludesUnavailableBarAndPartialCandleContinuesWithLivePrices()
    {
        var series = new ScriptBarSeries(60, 10);
        series.SeedHistory([
            History(0, 10, 11, 9, 10), History(15, 10, 12, 10, 11),
            History(30, 11, 13, 10, 12), History(45, 12, 900, 1, 500),
        ], Start.AddSeconds(59));
        Assert.Empty(series.Snapshot());
        Assert.Equal(0, series.Version);

        series.ObserveQuote(Quote(50, 14));
        Assert.Empty(series.Snapshot());
        series.ObserveQuote(Quote(60, 15));

        StrategyBar bar = Assert.Single(series.Snapshot());
        Assert.Equal(Start, bar.StartsAtUtc);
        Assert.Equal(Start.AddSeconds(60), bar.EndsAtUtc);
        Assert.Equal(10m, bar.Open);
        Assert.Equal(14m, bar.High);
        Assert.Equal(9m, bar.Low);
        Assert.Equal(14m, bar.Close);
        Assert.Equal(30m, bar.Volume);
        Assert.Equal(1, series.Version);
    }

    [Fact]
    public void HistoricalBarsBecomeAvailableAtTheirEnd()
    {
        var beforeEnd = new ScriptBarSeries(15, 10);
        var atEnd = new ScriptBarSeries(15, 10);
        MarketQuote quote = History(0, 10, 12, 9, 11);

        beforeEnd.SeedHistory([quote], Start.AddSeconds(14));
        atEnd.SeedHistory([quote], Start.AddSeconds(15));

        Assert.Empty(beforeEnd.Snapshot());
        Assert.Equal(Start.AddSeconds(15), Assert.Single(atEnd.Snapshot()).EndsAtUtc);
    }

    [Fact]
    public void DuplicateHistoricalBarAfterSeedingCannotRewriteCompletedHistory()
    {
        var series = new ScriptBarSeries(15, 10);
        series.SeedHistory([History(0, 10, 12, 9, 11)], Start.AddSeconds(15));
        long version = series.Version;

        series.ObserveHistoricalBar(History(0, 500, 900, 1, 800));

        Assert.Equal(11m, Assert.Single(series.Snapshot()).Close);
        Assert.Equal(version, series.Version);
    }

    [Fact]
    public void DuplicateAndOutOfOrderLivePricesDoNotRewriteFrozenBars()
    {
        var series = new ScriptBarSeries(60, 10);
        series.ObserveQuote(Quote(0, 10));
        series.ObserveQuote(Quote(10, 11));
        series.ObserveQuote(Quote(5, 900));
        series.ObserveQuote(Quote(10, 900));
        series.ObserveQuote(Quote(60, 12));
        IReadOnlyList<StrategyBar> frozen = series.Snapshot();
        series.ObserveQuote(Quote(20, 900));
        series.ObserveQuote(Quote(70, 13));

        StrategyBar bar = Assert.Single(frozen);
        Assert.Equal(10m, bar.Open);
        Assert.Equal(11m, bar.High);
        Assert.Equal(11m, bar.Close);
        Assert.Equal(bar, Assert.Single(series.Snapshot()));
        Assert.Equal(1, series.Version);
    }

    [Fact]
    public void StartupPartialLiveCandleIsExcluded()
    {
        var series = new ScriptBarSeries(60, 10);
        series.ObserveQuote(Quote(20, 900));
        series.ObserveQuote(Quote(60, 10));
        Assert.Empty(series.Snapshot());
        series.ObserveQuote(Quote(90, 11));
        series.ObserveQuote(Quote(120, 12));

        StrategyBar bar = Assert.Single(series.Snapshot());
        Assert.Equal(Start.AddSeconds(60), bar.StartsAtUtc);
        Assert.Equal(11m, bar.High);
        Assert.Equal(10m, bar.Open);
    }

    [Fact]
    public void GapsResetWarmupWithoutSynthesizingMissingHistoricalBars()
    {
        var series = new ScriptBarSeries(60, 10);
        foreach (int second in new[] { 0, 15, 30, 45 })
            series.ObserveHistoricalBar(History(second, 10, 11, 9, 10));
        Assert.Single(series.Snapshot());
        foreach (int second in new[] { 75, 90, 105 })
            series.ObserveHistoricalBar(History(second, 10, 11, 9, 10));
        Assert.Empty(series.Snapshot());
        foreach (int second in new[] { 120, 135, 150, 165 })
            series.ObserveHistoricalBar(History(second, 12, 13, 11, 12));

        Assert.Equal(Start.AddSeconds(120), Assert.Single(series.Snapshot()).StartsAtUtc);
    }

    [Fact]
    public void LiveGapResetsWarmupAndCapacityKeepsLatestCompletedBars()
    {
        var series = new ScriptBarSeries(15, 2);
        foreach (int second in new[] { 0, 15, 30, 45 }) series.ObserveQuote(Quote(second, 10));
        Assert.Equal(2, series.Snapshot().Count);
        Assert.Equal(Start.AddSeconds(15), series.Snapshot()[0].StartsAtUtc);

        series.ObserveQuote(Quote(90, 11));
        Assert.Empty(series.Snapshot());
        series.ObserveQuote(Quote(105, 12));
        Assert.Equal(Start.AddSeconds(90), Assert.Single(series.Snapshot()).StartsAtUtc);
    }

    private static MarketQuote Quote(int second, decimal price) =>
        new(new Instrument("SOFI"), Start.AddSeconds(second), Start.AddSeconds(second), price, price, price, 0);

    private static MarketQuote History(int second, decimal open, decimal high, decimal low, decimal close) =>
        Quote(second, close) with { OpenPrice = open, HighPrice = high, LowPrice = low, ClosePrice = close, Volume = 10 };
}
