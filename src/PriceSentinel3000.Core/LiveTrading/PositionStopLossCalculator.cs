using PriceSentinel3000.Core.Configuration;

namespace PriceSentinel3000.Core.LiveTrading;

public sealed record PositionStopLossAssessment(
    decimal ExitMark,
    decimal UnrealizedLoss,
    decimal LossLimit,
    decimal TriggerPrice,
    decimal DeclinePercentage,
    bool IsTriggered);

public static class PositionStopLossCalculator
{
    public static PositionStopLossAssessment Evaluate(
        TradingSessionSettings settings,
        BrokerPosition position,
        decimal exitMark)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(position);

        if (position.Quantity <= 0m)
        {
            throw new ArgumentException(
                "Stop-loss assessment requires a positive long position.",
                nameof(position));
        }

        if (position.AverageBuyPrice <= 0m)
        {
            throw new ArgumentException(
                "Stop-loss assessment requires a positive average purchase price.",
                nameof(position));
        }

        if (exitMark <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitMark),
                "The estimated exit price must be positive.");
        }

        decimal unrealizedLoss = Math.Max(
            0m,
            (position.AverageBuyPrice - exitMark) * position.Quantity);
        decimal lossLimit = settings.StopLossBasis switch
        {
            StopLossBasis.TotalPositionLossAmount => settings.StopLossValue,
            _ => position.AverageBuyPrice * position.Quantity *
                 settings.StopLossValue / 100m,
        };
        decimal triggerPrice = settings.StopLossBasis switch
        {
            StopLossBasis.TotalPositionLossAmount =>
                position.AverageBuyPrice - settings.StopLossValue / position.Quantity,
            _ => position.AverageBuyPrice * (1m - settings.StopLossValue / 100m),
        };
        decimal declinePercentage = Math.Max(
            0m,
            (position.AverageBuyPrice - exitMark) /
            position.AverageBuyPrice * 100m);

        return new(
            exitMark,
            unrealizedLoss,
            lossLimit,
            triggerPrice,
            declinePercentage,
            unrealizedLoss >= lossLimit);
    }
}
