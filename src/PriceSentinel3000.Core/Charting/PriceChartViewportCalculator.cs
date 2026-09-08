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
    private const int BaseCandleIntervalSeconds = 15;

    // Window minutes define density at 15 seconds; scale time, not candle width.
    public static TimeSpan GetVisibleDuration(int candleIntervalSeconds, double windowMinutes)
    {
        int normalizedIntervalSeconds = Math.Clamp(candleIntervalSeconds, 1, 3600);
        double normalizedWindowMinutes = double.IsFinite(windowMinutes)
            ? Math.Clamp(windowMinutes, 1d, 60d)
            : 7d;

        return TimeSpan.FromMinutes(
            normalizedWindowMinutes * normalizedIntervalSeconds / BaseCandleIntervalSeconds);
    }

    public static PriceChartTimeWindow CreateTimeWindow(
        DateTimeOffset latestCandleTimestamp,
        int candleIntervalSeconds,
        double windowMinutes)
    {
        TimeSpan candleInterval = TimeSpan.FromSeconds(
            Math.Clamp(candleIntervalSeconds, 1, 3600));
        TimeSpan visibleDuration = GetVisibleDuration(candleIntervalSeconds, windowMinutes);
        DateTimeOffset lastTimestamp = latestCandleTimestamp + candleInterval;

        return new(
            lastTimestamp - visibleDuration,
            lastTimestamp,
            candleInterval);
    }
}
