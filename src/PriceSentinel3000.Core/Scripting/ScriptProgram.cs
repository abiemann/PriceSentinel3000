namespace PriceSentinel3000.Core.Scripting;

internal sealed class ScriptProgram(
    Dictionary<string, Declaration> declarations,
    List<ScriptOrder> orders,
    List<Expr> decorations,
    List<Expr> ignoredNumericArguments)
{
    internal IReadOnlyDictionary<string, Declaration> Declarations { get; } = declarations;
    internal IReadOnlyList<ScriptOrder> Orders { get; } = orders;

    private static readonly HashSet<string> Prices = new(StringComparer.OrdinalIgnoreCase)
        { "open", "high", "low", "close", "hl2", "hlc3", "ohlc4" };
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yes"] = 1, ["true"] = 1, ["no"] = 0, ["false"] = 0, ["Double.NaN"] = double.NaN,
        ["AverageType.SIMPLE"] = 0, ["AverageType.EXPONENTIAL"] = 1, ["AverageType.WILDERS"] = 2,
        ["CrossingDirection.ANY"] = 0, ["CrossingDirection.ABOVE"] = 1, ["CrossingDirection.BELOW"] = 2,
    };
    private static readonly HashSet<string> VisualConstants = new(StringComparer.OrdinalIgnoreCase)
    {
        "Color.BLACK", "Color.BLUE", "Color.CURRENT", "Color.CYAN", "Color.DARK_GRAY", "Color.DARK_GREEN",
        "Color.DARK_ORANGE", "Color.DARK_RED", "Color.DOWNTICK", "Color.GRAY", "Color.GREEN", "Color.LIGHT_GRAY",
        "Color.LIGHT_GREEN", "Color.LIGHT_ORANGE", "Color.LIGHT_RED", "Color.LIME", "Color.MAGENTA", "Color.ORANGE",
        "Color.PINK", "Color.PLUM", "Color.RED", "Color.UPTICK", "Color.VIOLET", "Color.WHITE", "Color.YELLOW",
        "PaintingStrategy.LINE", "PaintingStrategy.HISTOGRAM", "PaintingStrategy.POINTS", "PaintingStrategy.DASHES",
        "PaintingStrategy.ARROW_UP", "PaintingStrategy.ARROW_DOWN", "PaintingStrategy.BOOLEAN_ARROW_UP",
        "PaintingStrategy.BOOLEAN_ARROW_DOWN", "PaintingStrategy.BOOLEAN_POINTS", "PaintingStrategy.SQUARES",
        "PaintingStrategy.TRIANGLES", "PaintingStrategy.LINE_VS_POINTS", "PaintingStrategy.LINE_VS_SQUARES",
        "PaintingStrategy.LINE_VS_TRIANGLES", "PaintingStrategy.VALUES_ABOVE", "PaintingStrategy.VALUES_BELOW",
        "Curve.FIRM", "Curve.SHORT_DASH", "Curve.MEDIUM_DASH", "Curve.LONG_DASH", "Curve.POINTS",
    };

    internal static bool IsBuiltin(string name) => Prices.Contains(name) || Constants.ContainsKey(name) ||
        VisualConstants.Contains(name) || name.Equals("volume", StringComparison.OrdinalIgnoreCase);
    internal static bool TryConstant(string name, out double value) => Constants.TryGetValue(name, out value);

    public void Validate()
    {
        var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numericCache = new Dictionary<Expr, int>(ReferenceEqualityComparer.Instance);
        var visualCache = new Dictionary<Expr, int>(ReferenceEqualityComparer.Instance);
        foreach (Declaration declaration in Declarations.Values) Visit(declaration.Value, false, path, 0, numericCache, visualCache);
        foreach (ScriptOrder order in Orders) Visit(order.Condition, false, path, 0, numericCache, visualCache);
        foreach (Expr argument in ignoredNumericArguments) Visit(argument, false, path, 0, numericCache, visualCache);
        foreach (Expr decoration in decorations) Visit(decoration, true, path, 0, numericCache, visualCache);
    }

    private int Visit(Expr expression, bool visual, HashSet<string> path, int depth,
        Dictionary<Expr, int> numericCache, Dictionary<Expr, int> visualCache)
    {
        if (depth > 64) throw new ScriptException(expression.Line, "Expression or declaration dependency depth exceeds 64 levels.");
        Dictionary<Expr, int> cache = visual ? visualCache : numericCache;
        if (cache.TryGetValue(expression, out int cached))
        {
            if (depth + cached > 64) throw new ScriptException(expression.Line, "Expression or declaration dependency depth exceeds 64 levels.");
            return cached;
        }
        int height = 0;
        switch (expression)
        {
            case TextExpr when !visual:
                throw new ScriptException(expression.Line, "Strings are supported only in ignored visual annotations and AddOrder names.");
            case NameExpr name:
                if (name.Name.Equals("volume", StringComparison.OrdinalIgnoreCase))
                    throw new ScriptException(name.Line, "Volume is unavailable for sampled live bars; volume-dependent scripts are incompatible.");
                if (Prices.Contains(name.Name) || Constants.ContainsKey(name.Name) || visual && VisualConstants.Contains(name.Name)) return 0;
                if (!Declarations.TryGetValue(name.Name, out Declaration? declaration))
                    throw new ScriptException(name.Line, $"Unknown or unsupported identifier '{name.Name}'.");
                if (!path.Add(name.Name))
                    throw new ScriptException(name.Line, $"Cyclic or recursive definition '{name.Name}' is unsupported, including historical self-references.");
                height = 1 + Visit(declaration.Value, false, path, depth + 1, numericCache, visualCache);
                path.Remove(name.Name);
                break;
            case HistoryExpr { Offset: < 0 }:
                throw new ScriptException(expression.Line, "Future history is prohibited. Only exact open[-1] in AddOrder's ignored execution-price argument is allowed.");
        }
        foreach (Expr child in Children(expression))
            height = Math.Max(height, 1 + Visit(child, visual, path, depth + 1, numericCache, visualCache));
        cache[expression] = height;
        return height;
    }

    internal Dictionary<string, decimal> GetDefaultInputs()
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (Declaration declaration in Declarations.Values.Where(item => item.IsInput))
        {
            double? value = ConstantValue(declaration.Value, result);
            if (value is null) continue; // Price-series inputs can be used, but cannot be overridden with numbers.
            if (!double.IsFinite(value.Value) || value > (double)decimal.MaxValue || value < (double)decimal.MinValue)
                throw new ScriptException(declaration.Line, $"Input '{declaration.Name}' must have a finite numeric default or a price-series expression.");
            try { result[declaration.Name] = (decimal)value.Value; }
            catch (OverflowException) { throw new ScriptException(declaration.Line, $"Input '{declaration.Name}' exceeds the supported numeric range."); }
        }
        return result;
    }

    internal double? ConstantValue(Expr expression, IReadOnlyDictionary<string, decimal> inputs)
    {
        var cache = new Dictionary<Expr, double?>(ReferenceEqualityComparer.Instance);
        double? Value(Expr node)
        {
            if (cache.TryGetValue(node, out double? cached)) return cached;
            double? result = node switch
            {
                NumberExpr number => number.Value,
                NameExpr name when TryConstant(name.Name, out double constant) => constant,
                NameExpr name when Declarations.TryGetValue(name.Name, out Declaration? declaration) =>
                    declaration.IsInput && inputs.TryGetValue(name.Name, out decimal supplied) ? (double)supplied : Value(declaration.Value),
                UnaryExpr unary when Value(unary.Value) is double value => Numeric.Unary(unary.Op, value),
                BinaryExpr binary when !binary.Op.StartsWith("cross", StringComparison.Ordinal) &&
                    Value(binary.Left) is double left && Value(binary.Right) is double right => Numeric.Binary(binary.Op, left, right),
                IfExpr conditional when Value(conditional.Condition) is double test && double.IsFinite(test) =>
                    Value(test != 0 ? conditional.WhenTrue : conditional.WhenFalse),
                CallExpr call when call.Name is "absvalue" or "min" or "max" or "sqr" or "sqrt" or "power" or "sign" or "isnan" => Function(call),
                _ => null,
            };
            cache[node] = result;
            return result;
        }
        double? Function(CallExpr call)
        {
            double?[] values = call.Args.Select(Value).ToArray();
            return values.All(value => value.HasValue) ? Numeric.Function(call.Name, values.Select(value => value!.Value).ToArray()) : null;
        }
        return Value(expression);
    }

    internal int Integer(Expr expression, IReadOnlyDictionary<string, decimal> inputs, int minimum, int maximum, string label)
    {
        double? value = ConstantValue(expression, inputs);
        if (value is null || !double.IsFinite(value.Value) || value < minimum || value > maximum || value != Math.Truncate(value.Value))
            throw new ScriptException(expression.Line, $"{label} must be a constant integer between {minimum} and {maximum} (numeric inputs are allowed).");
        return (int)value.Value;
    }

    internal int Warmup(IReadOnlyDictionary<string, decimal> inputs)
    {
        var cache = new Dictionary<Expr, int>(ReferenceEqualityComparer.Instance);
        int Needed(Expr expression)
        {
            if (cache.TryGetValue(expression, out int cached)) return cached;
            int count = expression switch
            {
                NameExpr name when Declarations.TryGetValue(name.Name, out Declaration? declaration) => Needed(declaration.Value),
                HistoryExpr history => Needed(history.Value) + history.Offset,
                BinaryExpr binary when binary.Op.StartsWith("cross", StringComparison.Ordinal) => Math.Max(Needed(binary.Left), Needed(binary.Right)) + 1,
                _ => Children(expression).Select(Needed).DefaultIfEmpty(1).Max(),
            };
            if (expression is CallExpr call)
            {
                int Period(int index) => Integer(call.Args[index], inputs, 1, ThinkScriptCompiler.MaximumPeriod, "Indicator length");
                int AverageWarmup(int type, int period) => type switch { 0 => period - 1, 1 => 4 * period - 1, _ => 7 * period - 1 };
                count = call.Name switch
                {
                    "average" or "highest" or "lowest" or "sum" => Needed(call.Args[0]) + Period(1) - 1,
                    "expaverage" => Needed(call.Args[0]) + AverageWarmup(1, Period(1)),
                    "wildersaverage" => Needed(call.Args[0]) + AverageWarmup(2, Period(1)),
                    "movingaverage" => Needed(call.Args[1]) + AverageWarmup(Integer(call.Args[0], inputs, 0, 2, "Average type"), Period(2)),
                    "rsi" => Needed(call.Args[3]) + AverageWarmup(Integer(call.Args[4], inputs, 0, 2, "RSI average type"), Period(0)) + 1,
                    "crosses" => Math.Max(Needed(call.Args[0]), Needed(call.Args[1])) + 1 + 0 * Integer(call.Args[2], inputs, 0, 2, "Crossing direction"),
                    "truerange" => count + 1,
                    "round" => count + 0 * Integer(call.Args[1], inputs, 0, 12, "Round digits"),
                    _ => count,
                };
            }
            if (count > ThinkScriptCompiler.MaximumBars)
                throw new ScriptException(expression.Line, $"Required warmup exceeds the {ThinkScriptCompiler.MaximumBars}-bar history limit.");
            cache[expression] = count;
            return count;
        }
        int required = Declarations.Values.Select(item => Needed(item.Value))
            .Concat(Orders.Select(item => Needed(item.Condition))).DefaultIfEmpty(1).Max();
        // Ignored expressions still have to obey the same language and parameter limits.
        foreach (Expr expression in decorations) Needed(expression);
        foreach (Expr expression in ignoredNumericArguments) Needed(expression);
        return required;
    }

    internal static IEnumerable<Expr> Children(Expr expression) => expression switch
    {
        UnaryExpr unary => [unary.Value], BinaryExpr binary => [binary.Left, binary.Right],
        IfExpr conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        HistoryExpr history => [history.Value], CallExpr call => call.Args, _ => [],
    };
}

internal static class Numeric
{
    internal static double Finite(double value) => double.IsFinite(value) ? value : double.NaN;
    internal static double Unary(string op, double value) => !double.IsFinite(value) ? double.NaN : op switch
        { "+" => value, "-" => -value, _ => value == 0 ? 1 : 0 };
    internal static double Binary(string op, double left, double right)
    {
        if (op is "and" or "&&" && (left == 0 || right == 0)) return 0;
        if (op is "or" or "||" && (double.IsFinite(left) && left != 0 || double.IsFinite(right) && right != 0)) return 1;
        if (!double.IsFinite(left) || !double.IsFinite(right)) return double.NaN;
        return Finite(op switch
        {
            "+" => left + right, "-" => left - right, "*" => left * right,
            "/" => right == 0 ? double.NaN : left / right, "%" => right == 0 ? double.NaN : left % right,
            ">" => left > right ? 1 : 0, "<" => left < right ? 1 : 0,
            ">=" => left >= right ? 1 : 0, "<=" => left <= right ? 1 : 0,
            "==" => left == right ? 1 : 0, "!=" or "<>" => left != right ? 1 : 0,
            "and" or "&&" => left != 0 && right != 0 ? 1 : 0,
            "or" or "||" => left != 0 || right != 0 ? 1 : 0, _ => double.NaN,
        });
    }
    internal static double Function(string name, double[] args)
    {
        if (name == "isnan") return double.IsNaN(args[0]) ? 1 : 0;
        if (args.Any(value => !double.IsFinite(value))) return double.NaN;
        return Finite(name switch
        {
            "absvalue" => Math.Abs(args[0]), "min" => Math.Min(args[0], args[1]), "max" => Math.Max(args[0], args[1]),
            "sqr" => args[0] * args[0], "sqrt" => Math.Sqrt(args[0]), "power" => Math.Pow(args[0], args[1]),
            "sign" => Math.Sign(args[0]), "round" => Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero), _ => double.NaN,
        });
    }
}
