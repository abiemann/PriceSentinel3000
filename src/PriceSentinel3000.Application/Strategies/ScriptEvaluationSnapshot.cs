using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.Application.Strategies;

/// <summary>The completed history and cached values from one actual script evaluation.</summary>
public sealed record ScriptEvaluationSnapshot(
    DateTimeOffset EvaluatedAtUtc,
    long BarVersion,
    long CompletedBarCount,
    int RetainedBars,
    int RequiredWarmupBars,
    StrategyBar? LatestBar,
    ScriptProposal Proposal)
{
    public string State => Proposal.State;
    public bool IsWarmingUp => State == "WARMING UP";
}
