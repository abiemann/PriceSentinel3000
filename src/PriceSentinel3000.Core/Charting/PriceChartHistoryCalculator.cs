using PriceSentinel3000.Core.Indicators;

namespace PriceSentinel3000.Core.Charting;

public static class PriceChartHistoryCalculator
{
    public static TimeSpan GetRsiLookback(int candleIntervalSeconds)
    {
        if (candleIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candleIntervalSeconds));
        }

        return TimeSpan.FromSeconds(
            candleIntervalSeconds * (double)SimpleRsiCalculator.DefaultPeriod);
    }
}
