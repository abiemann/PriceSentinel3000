using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.LiveTrading;

namespace PriceSentinel3000.Core.Tests.LiveTrading;

public sealed class PositionStopLossCalculatorTests
{
    [Theory]
    [InlineData(99.01, false)]
    [InlineData(99.00, true)]
    [InlineData(98.99, true)]
    public void Evaluate_PercentageStopUsesInclusiveBoundary(
        decimal exitMark,
        bool expectedTriggered)
    {
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            StopLossBasis = StopLossBasis.PurchasePriceDeclinePercentage,
            StopLossValue = 1m,
        };
        BrokerPosition position = Position(10m, 100m);

        PositionStopLossAssessment result = PositionStopLossCalculator.Evaluate(
            settings,
            position,
            exitMark);

        Assert.Equal(99m, result.TriggerPrice);
        Assert.Equal(expectedTriggered, result.IsTriggered);
    }

    [Theory]
    [InlineData(95.01, false)]
    [InlineData(95.00, true)]
    [InlineData(94.99, true)]
    public void Evaluate_FixedLossStopUsesInclusiveBoundary(
        decimal exitMark,
        bool expectedTriggered)
    {
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            StopLossBasis = StopLossBasis.TotalPositionLossAmount,
            StopLossValue = 50m,
        };
        BrokerPosition position = Position(10m, 100m);

        PositionStopLossAssessment result = PositionStopLossCalculator.Evaluate(
            settings,
            position,
            exitMark);

        Assert.Equal(95m, result.TriggerPrice);
        Assert.Equal(expectedTriggered, result.IsTriggered);
    }

    [Fact]
    public void Evaluate_RejectsInvalidPositionOrExitMark()
    {
        TradingSessionSettings settings = TradingSessionSettings.Default;

        Assert.Throws<ArgumentException>(() =>
            PositionStopLossCalculator.Evaluate(
                settings,
                Position(0m, 100m),
                100m));
        Assert.Throws<ArgumentException>(() =>
            PositionStopLossCalculator.Evaluate(
                settings,
                Position(10m, 0m),
                100m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PositionStopLossCalculator.Evaluate(
                settings,
                Position(10m, 100m),
                0m));
    }

    private static BrokerPosition Position(decimal quantity, decimal averagePrice) =>
        new("AMD", quantity, averagePrice, quantity, 0m);
}
