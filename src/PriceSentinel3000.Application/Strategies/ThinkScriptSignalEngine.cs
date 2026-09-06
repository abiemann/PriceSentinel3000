using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Application.Strategies;

/// <summary>Adapts a pinned price-only script to the host's existing risk pipeline.</summary>
public sealed class ThinkScriptSignalEngine : IPriceActionSignalEngine
{
    private readonly CompiledThinkScript _program;
    private long _evaluatedVersion = -1;

    public ThinkScriptSignalEngine(CompiledThinkScript program, int intervalSeconds)
    {
        _program = program ?? throw new ArgumentNullException(nameof(program));
        if (!program.IsCompatible)
            throw new ArgumentException("The selected script is incompatible.", nameof(program));
        Bars = new(intervalSeconds, Math.Min(4096, Math.Max(256, program.RequiredWarmupBars * 2)));
    }

    public ScriptBarSeries Bars { get; }
    public string? Fault { get; private set; }

    public StrategyDecision Evaluate(IReadOnlyList<MarketQuote> quotes, StrategyPositionContext position)
    {
        DateTimeOffset at = quotes.Count == 0 ? DateTimeOffset.MinValue : quotes[^1].SourceTimestampUtc;
        if (Fault is not null)
            return StrategyDecision.Hold(at, "SCRIPT ERROR", Fault);
        if (_evaluatedVersion == Bars.Version)
            return StrategyDecision.Hold(at, "WAITING FOR CANDLE", "Watching live prices; the next strategy candle has not closed.");
        IReadOnlyList<StrategyBar> bars = Bars.Snapshot();
        if (bars.Count > 0 && bars[^1].EndsAtUtc > at)
            return StrategyDecision.Hold(at, "WAITING FOR CANDLE", "The newest strategy candle is not yet available at this observation.");
        _evaluatedVersion = Bars.Version;
        try
        {
            ScriptProposal proposal = _program.Evaluate(bars, position);
            if (proposal.State == "SCRIPT ERROR")
                return Fail(at, proposal.Reason);
            StrategySignalKind signal = proposal.Action switch
            {
                ScriptAction.Buy => StrategySignalKind.Buy,
                ScriptAction.Sell => StrategySignalKind.Sell,
                _ => StrategySignalKind.Hold,
            };
            return new(at, signal, proposal.State, 0m, [proposal.Reason], null, 0m, 0m);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Fail(at, exception.Message);
        }
    }

    private StrategyDecision Fail(DateTimeOffset at, string reason)
    {
        Fault = $"Script evaluation stopped: {reason} Stop the session and review the script. Host risk checks remain active.";
        return StrategyDecision.Hold(at, "SCRIPT ERROR", Fault);
    }
}
