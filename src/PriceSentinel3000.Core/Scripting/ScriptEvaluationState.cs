namespace PriceSentinel3000.Core.Scripting;

/// <summary>Session-local checkpoints for CountSince; never share between trading sessions.</summary>
public sealed class ScriptEvaluationState
{
    private ScriptProgram? _program;
    private Dictionary<string, decimal> _inputs = [];
    private StrategyBar[] _bars = [];
    private Dictionary<CallExpr, double[]> _counts = new(ReferenceEqualityComparer.Instance);

    public void Reset()
    {
        _program = null;
        _inputs.Clear();
        _bars = [];
        _counts.Clear();
    }

    internal Frame Begin(ScriptProgram program, IReadOnlyDictionary<string, decimal> inputs,
        IReadOnlyList<StrategyBar> bars)
    {
        bool reuse = ReferenceEquals(_program, program) && _inputs.Count == inputs.Count &&
            inputs.All(item => _inputs.TryGetValue(item.Key, out decimal value) && value == item.Value) &&
            _bars.Length > 0 && bars.Count > 0 && bars[^1].EndsAtUtc >= _bars[^1].EndsAtUtc &&
            bars[0].StartsAtUtc >= _bars[0].StartsAtUtc;
        var previousIndices = new Dictionary<DateTimeOffset, int>();
        if (reuse)
        {
            for (int i = 0; i < _bars.Length; i++) previousIndices[_bars[i].EndsAtUtc] = i;
            bool overlaps = false;
            int previousMatch = -1;
            foreach (StrategyBar bar in bars)
            {
                if (!previousIndices.TryGetValue(bar.EndsAtUtc, out int index)) continue;
                overlaps = true;
                if (bar != _bars[index] || previousMatch >= 0 && index != previousMatch + 1)
                { reuse = false; break; }
                previousMatch = index;
            }
            // Never carry a counter across an unevaluated gap in the supplied history.
            reuse &= overlaps || bars[0].StartsAtUtc == _bars[^1].EndsAtUtc;
        }
        return new(this, program, inputs, bars, reuse ? _bars : [],
            reuse ? _counts : new(ReferenceEqualityComparer.Instance),
            reuse ? previousIndices : []);
    }

    internal sealed class Frame(ScriptEvaluationState owner, ScriptProgram program,
        IReadOnlyDictionary<string, decimal> inputs, IReadOnlyList<StrategyBar> bars,
        StrategyBar[] previousBars, Dictionary<CallExpr, double[]> previousCounts,
        Dictionary<DateTimeOffset, int> previousIndices)
    {
        private readonly Dictionary<CallExpr, double[]> _counts = new(ReferenceEqualityComparer.Instance);

        internal bool TryPrevious(CallExpr call, StrategyBar bar, out double value)
        {
            value = 0;
            if (!previousCounts.TryGetValue(call, out double[]? values) ||
                !previousIndices.TryGetValue(bar.EndsAtUtc, out int index)) return false;
            value = values[index];
            return true;
        }

        internal double Seed(CallExpr call) =>
            previousBars.Length > 0 && bars.Count > 0 && previousBars[^1].EndsAtUtc == bars[0].StartsAtUtc &&
            previousCounts.TryGetValue(call, out double[]? values) && double.IsFinite(values[^1]) ? values[^1] : 0;

        internal void Record(CallExpr call, double[] values) => _counts.Add(call, values);

        internal void Commit()
        {
            if (_counts.Count == 0) { owner.Reset(); return; }
            owner._program = program;
            owner._inputs = new(inputs, StringComparer.OrdinalIgnoreCase);
            owner._bars = bars.ToArray();
            // Each retained array was charged to the evaluator's bounded value budget.
            owner._counts = _counts;
        }
    }
}
