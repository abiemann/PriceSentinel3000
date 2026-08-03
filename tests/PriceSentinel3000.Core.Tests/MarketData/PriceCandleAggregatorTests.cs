using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.Tests.MarketData;

public sealed class PriceCandleAggregatorTests
{
    private static readonly Instrument Instrument = new("USO");
    private static readonly DateTimeOffset Start =
        new(2026, 7, 31, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Aggregate_PreservesTrueHistoricalOhlc()
    {
        MarketQuote historical = Quote(Start, 130.2m, 200m) with
        {
            OpenPrice = 130.1m,
            HighPrice = 130.5m,
            LowPrice = 129.9m,
            ClosePrice = 130.2m,
        };

        PriceCandle candle = Assert.Single(
            PriceCandleAggregator.Aggregate(
                [historical],
                TimeSpan.FromSeconds(15)));

        Assert.Equal(130.1m, candle.Open);
        Assert.Equal(130.5m, candle.High);
        Assert.Equal(129.9m, candle.Low);
        Assert.Equal(130.2m, candle.Close);
        Assert.Equal(200m, candle.Volume);
    }

    [Fact]
    public void Aggregate_BuildsFifteenSecondCandleFromLiveQuotes()
    {
        MarketQuote[] quotes =
        [
            Quote(Start.AddSeconds(1), 130.0m, 10m),
            Quote(Start.AddSeconds(6), 130.4m, 20m),
            Quote(Start.AddSeconds(11), 129.8m, 30m),
            Quote(Start.AddSeconds(16), 130.2m, 40m),
        ];

        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            quotes,
            TimeSpan.FromSeconds(15));

        Assert.Equal(2, candles.Count);
        Assert.Equal(130.0m, candles[0].Open);
        Assert.Equal(130.4m, candles[0].High);
        Assert.Equal(129.8m, candles[0].Low);
        Assert.Equal(129.8m, candles[0].Close);
        Assert.Equal(60m, candles[0].Volume);
        Assert.Equal(130.2m, candles[1].Open);
    }

    [Theory]
    [InlineData(30, 2)]
    [InlineData(60, 1)]
    [InlineData(120, 1)]
    public void Aggregate_CombinesQuotesAtSelectableChartIntervals(
        int intervalSeconds,
        int expectedCount)
    {
        MarketQuote[] quotes =
        [
            Quote(Start.AddSeconds(1), 130.0m, 10m),
            Quote(Start.AddSeconds(16), 130.4m, 20m),
            Quote(Start.AddSeconds(31), 129.8m, 30m),
            Quote(Start.AddSeconds(46), 130.2m, 40m),
        ];

        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            quotes,
            TimeSpan.FromSeconds(intervalSeconds));

        Assert.Equal(expectedCount, candles.Count);
        Assert.Equal(130.0m, candles[0].Open);
        Assert.Equal(130.4m, candles[0].High);
    }

    [Fact]
    public void Aggregate_FillsMissingIntervalsUsingPreviousClose()
    {
        MarketQuote[] quotes =
        [
            Quote(Start.AddSeconds(1), 130.0m, 10m),
            Quote(Start.AddSeconds(61), 131.0m, 20m),
        ];

        IReadOnlyList<PriceCandle> candles = PriceCandleAggregator.Aggregate(
            quotes,
            TimeSpan.FromSeconds(15));

        Assert.Equal(5, candles.Count);
        Assert.False(candles[0].IsSynthetic);
        Assert.False(candles[4].IsSynthetic);

        foreach (PriceCandle synthetic in candles.Skip(1).Take(3))
        {
            Assert.True(synthetic.IsSynthetic);
            Assert.Equal(0, synthetic.QuoteCount);
            Assert.Equal(130.0m, synthetic.Open);
            Assert.Equal(130.0m, synthetic.High);
            Assert.Equal(130.0m, synthetic.Low);
            Assert.Equal(130.0m, synthetic.Close);
            Assert.Equal(0m, synthetic.Volume);
        }

        Assert.Equal(Start.AddSeconds(15), candles[1].StartsAtUtc);
        Assert.Equal(Start.AddSeconds(30), candles[2].StartsAtUtc);
        Assert.Equal(Start.AddSeconds(45), candles[3].StartsAtUtc);
    }

    private static MarketQuote Quote(
        DateTimeOffset timestamp,
        decimal last,
        decimal volume) =>
        new(
            Instrument,
            timestamp,
            timestamp,
            last - 0.01m,
            last + 0.01m,
            last,
            volume);
}
