namespace PriceSentinel3000.Core.Indicators;

/// <summary>
/// Calculates a simple relative strength index from a fixed number of price changes.
/// </summary>
public static class SimpleRsiCalculator
{
    public const int DefaultPeriod = 14;

    public static decimal? Calculate(
        IReadOnlyList<decimal> prices,
        int period = DefaultPeriod)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ValidatePeriod(period);

        return prices.Count < period + 1
            ? null
            : CalculateWindow(prices, prices.Count - period - 1, period);
    }

    public static IReadOnlyList<decimal?> CalculateSeries(
        IReadOnlyList<decimal> prices,
        int period = DefaultPeriod)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ValidatePeriod(period);

        var values = new decimal?[prices.Count];

        for (int index = period; index < prices.Count; index++)
        {
            values[index] = CalculateWindow(prices, index - period, period);
        }

        return values;
    }

    private static decimal CalculateWindow(
        IReadOnlyList<decimal> prices,
        int firstPriceIndex,
        int period)
    {
        decimal totalGain = 0m;
        decimal totalLoss = 0m;

        for (int index = firstPriceIndex + 1;
             index <= firstPriceIndex + period;
             index++)
        {
            decimal change = prices[index] - prices[index - 1];

            if (change > 0m)
            {
                totalGain += change;
            }
            else
            {
                totalLoss -= change;
            }
        }

        if (totalLoss == 0m)
        {
            return totalGain == 0m ? 50m : 100m;
        }

        if (totalGain == 0m)
        {
            return 0m;
        }

        return 100m - 100m / (1m + totalGain / totalLoss);
    }

    private static void ValidatePeriod(int period)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
    }
}
