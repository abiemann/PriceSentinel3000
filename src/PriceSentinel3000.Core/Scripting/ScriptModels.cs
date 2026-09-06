using System.Collections.ObjectModel;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Scripting;

public enum ScriptAction { Hold, Buy, Sell }

public sealed record StrategyBar(
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record ScriptDiagnostic(int Line, string Message, bool IsError = true);

public sealed record ScriptIndicatorValue(string Name, string Kind, decimal? Value, string State);

public sealed record ScriptProposal(
    ScriptAction Action,
    string State,
    string Reason,
    IReadOnlyDictionary<string, decimal?> Plots)
{
    public const int MaximumIndicatorValues = 64;
    public IReadOnlyList<ScriptIndicatorValue> Indicators { get; init; } = [];
    public int IndicatorCount { get; init; }
    public bool IndicatorsTruncated => IndicatorCount > Indicators.Count;
    public int IndicatorLimit => MaximumIndicatorValues;
}

public sealed class CompiledThinkScript
{
    private readonly ScriptProgram? _program;

    internal CompiledThinkScript(ScriptProgram? program, IEnumerable<ScriptDiagnostic> diagnostics)
    {
        _program = program;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        DefaultInputs = new ReadOnlyDictionary<string, decimal>(
            program?.GetDefaultInputs() ?? new(StringComparer.OrdinalIgnoreCase));
        RequiredWarmupBars = program?.Warmup(DefaultInputs) ?? 0;
    }

    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }
    public bool IsCompatible => _program is not null && !Diagnostics.Any(item => item.IsError);
    public IReadOnlyDictionary<string, decimal> DefaultInputs { get; }
    public int RequiredWarmupBars { get; }

    public ScriptProposal Evaluate(
        IReadOnlyList<StrategyBar> bars,
        StrategyPositionContext position,
        IReadOnlyDictionary<string, decimal>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(position);
        if (!IsCompatible)
        {
            return Hold("SCRIPT ERROR", "This script is incompatible; inspect its compilation diagnostics.");
        }

        try
        {
            var parameters = new Dictionary<string, decimal>(DefaultInputs, StringComparer.OrdinalIgnoreCase);
            if (inputs is not null)
            {
                foreach ((string name, decimal value) in inputs)
                {
                    if (!parameters.ContainsKey(name))
                    {
                        throw new ScriptException(1, $"Unknown numeric input '{name}'.");
                    }
                    parameters[name] = value;
                }
            }

            return new ScriptEvaluator(_program!, bars, parameters).Evaluate(position);
        }
        catch (ScriptException exception)
        {
            return Hold("SCRIPT ERROR", $"Line {exception.Line}: {exception.Message}");
        }
    }

    internal static ScriptProposal Hold(string state, string reason) =>
        new(ScriptAction.Hold, state, reason, ReadOnlyDictionary<string, decimal?>.Empty);
}
