namespace PriceSentinel3000.Core.Charting;

public readonly record struct PriceChartTimeWindow(
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp,
    TimeSpan CandleInterval)
{
    public DateTimeOffset GetCandleCenter(DateTimeOffset timestamp) =>
        timestamp + CandleInterval / 2d;

    public bool ContainsCandle(DateTimeOffset timestamp)
    {
        DateTimeOffset center = GetCandleCenter(timestamp);
        return center >= FirstTimestamp && center <= LastTimestamp;
    }
}

public static class PriceChartViewportCalculator
{
    public static PriceChartTimeWindow CreateTimeWindow(
        DateTimeOffset latestCandleTimestamp,
        int candleIntervalSeconds,
        double windowMinutes)
    {
        TimeSpan candleInterval = TimeSpan.FromSeconds(
            Math.Clamp(candleIntervalSeconds, 1, 3600));
        double normalizedWindowMinutes = double.IsFinite(windowMinutes)
            ? Math.Clamp(windowMinutes, 1d, 60d)
            : 7d;
        DateTimeOffset lastTimestamp = latestCandleTimestamp + candleInterval;

        return new(
            lastTimestamp.AddMinutes(-normalizedWindowMinutes),
            lastTimestamp,
            candleInterval);
    }
}
