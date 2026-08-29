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
        Assert.Equal(LatestCandle.AddMinutes(-7), window.FirstTimestamp);
        Assert.True(window.ContainsCandle(LatestCandle));
    }

    [Fact]
    public void CreateTimeWindow_UsesFallbackForNonFiniteWindow()
    {
        PriceChartTimeWindow window = PriceChartViewportCalculator.CreateTimeWindow(
            LatestCandle,
            candleIntervalSeconds: 15,
            windowMinutes: double.NaN);

        Assert.Equal(
            window.LastTimestamp.AddMinutes(-7),
            window.FirstTimestamp);
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
