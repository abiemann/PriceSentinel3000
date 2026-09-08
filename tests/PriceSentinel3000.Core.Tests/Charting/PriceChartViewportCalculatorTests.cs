using PriceSentinel3000.Core.Charting;

namespace PriceSentinel3000.Core.Tests.Charting;

public sealed class PriceChartViewportCalculatorTests
{
    private static readonly DateTimeOffset LatestCandle =
        new(2026, 8, 2, 20, 51, 0, TimeSpan.Zero);

    [Fact]
    public void CreateTimeWindow_EndsAfterTheLatestCandle()
    {
        PriceChartTimeWindow window = PriceChartViewportCalculator.CreateTimeWindow(
            LatestCandle,
            candleIntervalSeconds: 120,
            windowMinutes: 9d);

        Assert.Equal(TimeSpan.FromMinutes(2), window.CandleInterval);
        Assert.Equal(LatestCandle.AddMinutes(2), window.LastTimestamp);
        Assert.Equal(LatestCandle.AddMinutes(-70), window.FirstTimestamp);
        Assert.True(window.ContainsCandle(LatestCandle));
        Assert.True(window.GetCandleCenter(LatestCandle) < window.LastTimestamp);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void CreateTimeWindow_UsesFallbackForNonFiniteWindow(double windowMinutes)
    {
        PriceChartTimeWindow window = PriceChartViewportCalculator.CreateTimeWindow(
            LatestCandle,
            candleIntervalSeconds: 15,
            windowMinutes);

        Assert.Equal(
            window.LastTimestamp.AddMinutes(-7),
            window.FirstTimestamp);
    }

    [Theory]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    public void CreateTimeWindow_KeepsSixtyCandleSlotsWhenTheIntervalChanges(
        int candleIntervalSeconds,
        double expectedMinutes)
    {
        PriceChartTimeWindow window = PriceChartViewportCalculator.CreateTimeWindow(
            LatestCandle,
            candleIntervalSeconds,
            windowMinutes: 15d);

        TimeSpan duration = window.LastTimestamp - window.FirstTimestamp;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), duration);
        Assert.Equal(60d, duration / window.CandleInterval);
        Assert.True(window.ContainsCandle(LatestCandle - window.CandleInterval * 59d));
        Assert.False(window.ContainsCandle(LatestCandle - window.CandleInterval * 60d));
    }

    [Theory]
    [InlineData(120, 0, 8)]
    [InlineData(120, 61, 480)]
    [InlineData(120, double.NaN, 56)]
    [InlineData(0, 15, 1)]
    [InlineData(7200, 15, 3600)]
    public void GetVisibleDuration_NormalizesInputsBeforeScaling(
        int candleIntervalSeconds,
        double windowMinutes,
        double expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            PriceChartViewportCalculator.GetVisibleDuration(candleIntervalSeconds, windowMinutes));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7200, 3600)]
    public void CreateTimeWindow_ClampsCandleInterval(
        int requestedSeconds,
        int expectedSeconds)
    {
        PriceChartTimeWindow window = PriceChartViewportCalculator.CreateTimeWindow(
            LatestCandle,
            requestedSeconds,
            windowMinutes: 7d);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), window.CandleInterval);
    }
}
