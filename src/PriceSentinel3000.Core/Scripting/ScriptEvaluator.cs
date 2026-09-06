using System.Collections.ObjectModel;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Scripting;

internal sealed class ScriptEvaluator(
    ScriptProgram program,
    IReadOnlyList<StrategyBar> bars,
    IReadOnlyDictionary<string, decimal> inputs)
{
    private const int MaximumOperations = 4_000_000;
    private const int MaximumValues = 2_000_000;
    private readonly Dictionary<Expr, double[]> _cache = new(ReferenceEqualityComparer.Instance);
    private int _operations;
    private int _values;

    internal ScriptProposal Evaluate(StrategyPositionContext position)
    {
        ScriptProposal proposal;
        try
        {
            proposal = EvaluateStrategy(position);
        }
        catch (ScriptException exception)
        {
            proposal = CompiledThinkScript.Hold("SCRIPT ERROR", $"Line {exception.Line}: {exception.Message}");
        }
        return CaptureIndicators(proposal);
    }

    private ScriptProposal EvaluateStrategy(StrategyPositionContext position)
    {
        if (bars.Count > ThinkScriptCompiler.MaximumBars)
            throw new ScriptException(1, $"Evaluation exceeds the {ThinkScriptCompiler.MaximumBars}-bar history limit.");
        ValidateBars();
        int warmup = program.Warmup(inputs);
        if (bars.Count < warmup)
            return CompiledThinkScript.Hold("WARMING UP", $"Script requires {warmup} completed bars; received {bars.Count}.");

        var plots = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (Declaration declaration in program.Declarations.Values.Where(item => item.IsPlot))
        {
            double value = Values(declaration.Value)[^1];
            decimal? plotted = null;
            if (double.IsFinite(value) && value < (double)decimal.MaxValue && value > (double)decimal.MinValue)
                plotted = (decimal)value;
            plots[declaration.Name] = plotted;
        }
        ScriptOrder[] triggered = program.Orders.Where(order => IsTrue(Values(order.Condition)[^1])).ToArray();
        bool buy = triggered.Any(order => order.Action == ScriptAction.Buy);
        bool sell = triggered.Any(order => order.Action == ScriptAction.Sell);
        var immutablePlots = new ReadOnlyDictionary<string, decimal?>(plots);
        if (buy && sell) return new(ScriptAction.Hold, "CONFLICTING SIGNALS", "Both entry and exit conditions are true; no action is proposed.", immutablePlots);
        ScriptAction action = buy && !position.HasPosition ? ScriptAction.Buy : sell && position.HasPosition ? ScriptAction.Sell : ScriptAction.Hold;
        string reason = action != ScriptAction.Hold ? triggered.First(order => order.Action == action).Name
            : buy ? "Entry condition is true, but a long position already exists."
            : sell ? "Exit condition is true, but there is no long position to close."
            : "No entry or exit condition is true on the completed bar.";
        return new(action, action == ScriptAction.Hold ? "HOLD" : action == ScriptAction.Buy ? "BUY SIGNAL" : "SELL SIGNAL", reason, immutablePlots);
    }

    private ScriptProposal CaptureIndicators(ScriptProposal proposal)
    {
        var indicators = new List<ScriptIndicatorValue>();
        int count = 0;
        foreach (Declaration declaration in program.Declarations.Values.Where(item => !item.IsInput))
        {
            count++;
            if (indicators.Count == ScriptProposal.MaximumIndicatorValues) continue;
            decimal? value = null;
            string state = proposal.State == "WARMING UP" ? "warming_up" : "not_evaluated";
            // Read only completed cache entries. Telemetry must never evaluate an
            // unused declaration or consume the strategy's operation budget.
            if (_cache.TryGetValue(declaration.Value, out double[]? cached) && cached.Length > 0)
            {
                double current = cached[^1];
                if (double.IsFinite(current) && current < (double)decimal.MaxValue && current > (double)decimal.MinValue)
                    value = (decimal)current;
                state = value.HasValue ? "available" : "unavailable";
            }
            indicators.Add(new(declaration.Name, declaration.IsPlot ? "plot" : "def", value, state));
        }
        return proposal with { Indicators = indicators.AsReadOnly(), IndicatorCount = count };
    }

    private void ValidateBars()
    {
        TimeSpan? duration = null;
        StrategyBar? previous = null;
        foreach (StrategyBar bar in bars)
        {
            if (bar is null || bar.EndsAtUtc <= bar.StartsAtUtc || bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0 ||
                bar.Low > Math.Min(bar.Open, bar.Close) || bar.High < Math.Max(bar.Open, bar.Close) ||
                previous is not null && bar.StartsAtUtc < previous.EndsAtUtc ||
                duration.HasValue && bar.EndsAtUtc - bar.StartsAtUtc != duration)
                throw new ScriptException(1, "Bars must contain valid positive OHLC prices, equal durations, and chronological non-overlapping completed intervals.");
            duration = bar.EndsAtUtc - bar.StartsAtUtc;
            previous = bar;
        }
    }

    private double[] Allocate(int line)
    {
        _values += bars.Count;
        if (_values > MaximumValues) throw new ScriptException(line, "Script evaluation exceeded its bounded memory budget.");
        return new double[bars.Count];
    }
    private void Spend(int count, int line)
    {
        _operations += count;
        if (_operations > MaximumOperations) throw new ScriptException(line, "Script evaluation exceeded its bounded operation budget.");
    }

    private double[] Values(Expr expression)
    {
        if (_cache.TryGetValue(expression, out double[]? cached)) return cached;
        double[] result;
        if (expression is NameExpr name && program.Declarations.TryGetValue(name.Name, out Declaration? declaration))
        {
            if (declaration.IsInput && inputs.TryGetValue(name.Name, out decimal supplied))
            {
                result = Allocate(expression.Line);
                Array.Fill(result, (double)supplied);
            }
            else result = Values(declaration.Value);
        }
        else if (expression is CallExpr call) result = Call(call);
        else
        {
            result = Allocate(expression.Line);
            double[][] children = ScriptProgram.Children(expression).Select(Values).ToArray();
            Spend(bars.Count, expression.Line);
            for (int i = 0; i < bars.Count; i++)
            {
                result[i] = expression switch
                {
                    NumberExpr number => number.Value,
                    NameExpr builtin => PriceOrConstant(builtin.Name, i),
                    UnaryExpr unary => Numeric.Unary(unary.Op, children[0][i]),
                    BinaryExpr binary when binary.Op.StartsWith("cross", StringComparison.Ordinal) =>
                        Cross(children[0], children[1], i, binary.Op == "crossabove" ? 1 : binary.Op == "crossbelow" ? 2 : 0),
                    BinaryExpr binary => Numeric.Binary(binary.Op, children[0][i], children[1][i]),
                    IfExpr => !double.IsFinite(children[0][i]) ? double.NaN : children[0][i] != 0 ? children[1][i] : children[2][i],
                    HistoryExpr history => i < history.Offset ? double.NaN : children[0][i - history.Offset],
                    _ => double.NaN,
                };
            }
        }
        _cache.Add(expression, result);
        return result;
    }

    private double PriceOrConstant(string name, int index)
    {
        if (ScriptProgram.TryConstant(name, out double constant)) return constant;
        StrategyBar bar = bars[index];
        return name.ToLowerInvariant() switch
        {
            "open" => (double)bar.Open, "high" => (double)bar.High, "low" => (double)bar.Low, "close" => (double)bar.Close,
            "hl2" => ((double)bar.High + (double)bar.Low) / 2,
            "hlc3" => ((double)bar.High + (double)bar.Low + (double)bar.Close) / 3,
            "ohlc4" => ((double)bar.Open + (double)bar.High + (double)bar.Low + (double)bar.Close) / 4,
            _ => double.NaN,
        };
    }

    private double[] Call(CallExpr call)
    {
        int Period(int index) => program.Integer(call.Args[index], inputs, 1, ThinkScriptCompiler.MaximumPeriod, "Indicator length");
        switch (call.Name)
        {
            case "average": case "sum": case "highest": case "lowest":
                return Rolling(Values(call.Args[0]), Period(1), call.Name, call.Line);
            case "expaverage": return Smooth(Values(call.Args[0]), Period(1), 1, call.Line);
            case "wildersaverage": return Smooth(Values(call.Args[0]), Period(1), 2, call.Line);
            case "movingaverage":
                return Smooth(Values(call.Args[1]), Period(2), program.Integer(call.Args[0], inputs, 0, 2, "Average type"), call.Line);
            case "rsi": return Rsi(call, Period(0));
        }
        double[][] args = call.Args.Select(Values).ToArray();
        double[] result = Allocate(call.Line);
        double[] current = new double[args.Length];
        Spend(bars.Count, call.Line);
        for (int i = 0; i < bars.Count; i++)
        {
            if (call.Name == "crosses") result[i] = Cross(args[0], args[1], i, (int)args[2][i]);
            else if (call.Name == "truerange")
                result[i] = i == 0 || !double.IsFinite(args[1][i - 1]) ? double.NaN
                    : Numeric.Finite(Math.Max(args[0][i], args[1][i - 1]) - Math.Min(args[2][i], args[1][i - 1]));
            else
            {
                for (int j = 0; j < args.Length; j++) current[j] = args[j][i];
                result[i] = Numeric.Function(call.Name, current);
            }
        }
        return result;
    }

    private double[] Rolling(double[] values, int period, string name, int line)
    {
        double[] result = Allocate(line);
        Array.Fill(result, double.NaN);
        // Period and operation budgets keep even extrema over adversarial inputs bounded.
        for (int i = period - 1; i < values.Length; i++)
        {
            Spend(period, line);
            double value = name == "highest" ? double.NegativeInfinity : name == "lowest" ? double.PositiveInfinity : 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                if (!double.IsFinite(values[j])) { value = double.NaN; break; }
                value = name == "highest" ? Math.Max(value, values[j]) : name == "lowest" ? Math.Min(value, values[j]) : value + values[j];
            }
            result[i] = Numeric.Finite(name == "average" ? value / period : value);
        }
        return result;
    }

    private double[] Smooth(double[] values, int period, int type, int line)
    {
        if (type == 0) return Rolling(values, period, "average", line);
        double[] result = Allocate(line);
        double alpha = type == 1 ? 2d / (period + 1) : 1d / period;
        double previous = double.NaN;
        int count = 0;
        double seed = 0;
        Spend(values.Length, line);
        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];
            if (!double.IsFinite(value)) { previous = double.NaN; count = 0; seed = 0; }
            else if (double.IsFinite(previous)) previous = Numeric.Finite(alpha * value + (1 - alpha) * previous);
            else if (type == 1) previous = value;
            else
            {
                seed += value;
                if (++count == period) previous = Numeric.Finite(seed / period);
            }
            result[i] = previous;
        }
        return result;
    }

    private double[] Rsi(CallExpr call, int period)
    {
        double[] prices = Values(call.Args[3]);
        double[] gains = Allocate(call.Line);
        double[] losses = Allocate(call.Line);
        Spend(bars.Count, call.Line);
        gains[0] = losses[0] = double.NaN;
        for (int i = 1; i < prices.Length; i++)
        {
            double change = prices[i] - prices[i - 1];
            gains[i] = Numeric.Finite(Math.Max(change, 0));
            losses[i] = Numeric.Finite(Math.Max(-change, 0));
        }
        int type = program.Integer(call.Args[4], inputs, 0, 2, "RSI average type");
        double[] averageGains = Smooth(gains, period, type, call.Line);
        double[] averageLosses = Smooth(losses, period, type, call.Line);
        double[] result = Allocate(call.Line);
        Spend(bars.Count, call.Line);
        for (int i = 0; i < result.Length; i++)
        {
            double gain = averageGains[i], loss = averageLosses[i];
            result[i] = !double.IsFinite(gain) || !double.IsFinite(loss) ? double.NaN
                : gain == 0 && loss == 0 ? 50 : loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
        }
        return result;
    }

    private static double Cross(double[] left, double[] right, int i, int direction)
    {
        if (i == 0 || !double.IsFinite(left[i]) || !double.IsFinite(right[i]) || !double.IsFinite(left[i - 1]) || !double.IsFinite(right[i - 1]))
            return double.NaN;
        bool above = left[i] > right[i] && left[i - 1] <= right[i - 1];
        bool below = left[i] < right[i] && left[i - 1] >= right[i - 1];
        return (direction == 1 ? above : direction == 2 ? below : above || below) ? 1 : 0;
    }
    private static bool IsTrue(double value) => double.IsFinite(value) && value != 0;
}
