namespace PriceSentinel3000.Core.Strategy;

public static class ReentryPriceGate
{
    public const decimal MinimumMovementPercentage = 0.10m;

    public static decimal MovementPercentage(
        decimal currentPrice,
        decimal previousSellPrice)
    {
        if (currentPrice <= 0m || previousSellPrice <= 0m)
        {
            return 0m;
        }

        return Math.Abs(currentPrice - previousSellPrice) /
               previousSellPrice * 100m;
    }

    public static bool IsSatisfied(
        decimal currentPrice,
        decimal previousSellPrice) =>
        MovementPercentage(currentPrice, previousSellPrice) >=
        MinimumMovementPercentage;
}
