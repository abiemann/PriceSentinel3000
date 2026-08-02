namespace PriceSentinel3000.Core.Strategy;

public enum StrategySignalKind
{
    Hold,
    Buy,
    Sell,
    StopLoss,
    DailyLoss,
}

public sealed record StrategyDecision(
    DateTimeOffset EvaluatedAtUtc,
    StrategySignalKind Signal,
    string State,
    decimal Confidence,
    IReadOnlyList<string> Reasons,
    decimal? SimpleRsi,
    decimal MomentumPercent,
    decimal ReferenceMovePercent)
{
    public static StrategyDecision Hold(
        DateTimeOffset evaluatedAtUtc,
        string state,
        string reason,
        decimal? simpleRsi = null,
        decimal momentumPercent = 0m,
        decimal referenceMovePercent = 0m) =>
        new(
            evaluatedAtUtc,
            StrategySignalKind.Hold,
            state,
            0m,
            [reason],
            simpleRsi,
            momentumPercent,
            referenceMovePercent);
}
