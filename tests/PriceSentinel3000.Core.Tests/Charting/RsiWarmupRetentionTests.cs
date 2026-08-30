using PriceSentinel3000.Core.Charting;
using PriceSentinel3000.Core.Indicators;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.Tests.Charting;

public sealed class RsiWarmupRetentionTests
{
    private static readonly Instrument Instrument = new("UVXY");
    private static readonly DateTimeOffset Start =
        new(2026, 8, 28, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(15, 210)]
    [InlineData(30, 420)]
    [InlineData(60, 840)]
    [InlineData(120, 1_680)]
    public void GetRsiLookback_ScalesWithSelectedCandleInterval(
        int intervalSeconds,
        int expectedWarmupSeconds)
    {
        TimeSpan result = PriceChartHistoryCalculator.GetRsiLookback(
            intervalSeconds);

        Assert.Equal(TimeSpan.FromSeconds(expectedWarmupSeconds), result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetRsiLookback_RejectsNonPositiveInterval(int intervalSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PriceChartHistoryCalculator.GetRsiLookback(intervalSeconds));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void DynamicWarmup_MakesRsiAvailableAtLeftViewportEdge(
        int intervalSeconds)
    {
        TimeSpan interval = TimeSpan.FromSeconds(intervalSeconds);
        TimeSpan visibleWindow = TimeSpan.FromMinutes(15);
        TimeSpan warmup = PriceChartHistoryCalculator.GetRsiLookback(
            intervalSeconds);
        var buffer = new PriceRingBuffer(
            Instrument,
            visibleWindow + warmup);
        int quoteCount = (int)((visibleWindow + warmup) / interval) + 1;

        buffer.Merge(
        [
            .. Enumerable.Range(0, quoteCount)
                .Select(index => Quote(
                    Start + interval * index,
                    100m + index)),
        ]);

        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            buffer.Snapshot(),
            interval);
        IReadOnlyList<decimal?> rsiValues = SimpleRsiCalculator.CalculateSeries(
            candles.Select(candle => candle.Close).ToArray());
        PriceChartTimeWindow viewport =
            PriceChartViewportCalculator.CreateTimeWindow(
                candles[^1].StartsAtUtc,
                intervalSeconds,
                visibleWindow.TotalMinutes);
        int firstVisibleIndex = Enumerable.Range(0, candles.Count)
            .First(index => viewport.ContainsCandle(candles[index].StartsAtUtc));

        Assert.NotNull(rsiValues[firstVisibleIndex]);
    }

    private static MarketQuote Quote(DateTimeOffset timestamp, decimal last) =>
        new(
            Instrument,
            timestamp,
            timestamp,
            last - 0.01m,
            last + 0.01m,
            last,
            100m);
}
