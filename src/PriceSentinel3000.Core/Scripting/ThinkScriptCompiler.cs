using System.Globalization;

namespace PriceSentinel3000.Core.Scripting;

/// <summary>A bounded language parser. It never compiles or invokes CLR source code.</summary>
public static class ThinkScriptCompiler
{
    public const string RuntimeVersion = "thinkscript-subset-v1";
    public const int MaximumSourceLength = 128_000;
    public const int MaximumBars = 4096;
    internal const int MaximumNodes = 4096;
    internal const int MaximumPeriod = 2048;

    public static CompiledThinkScript Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var diagnostics = new List<ScriptDiagnostic>();
        try
        {
            if (source.Length > MaximumSourceLength)
            {
                throw new ScriptException(1, $"Source exceeds the {MaximumSourceLength}-character limit.");
            }
            ScriptProgram program = new ScriptParser(source, diagnostics).Parse();
            program.Validate();
            return new(program, diagnostics);
        }
        catch (ScriptException exception)
        {
            diagnostics.Add(new(exception.Line, exception.Message));
            return new(null, diagnostics);
        }
    }
}

internal sealed class ScriptException(int line, string message) : Exception(message)
{
    public int Line { get; } = line;
}

internal abstract record Expr(int Line);
internal sealed record NumberExpr(double Value, int At) : Expr(At);
internal sealed record TextExpr(string Value, int At) : Expr(At);
internal sealed record NameExpr(string Name, int At) : Expr(At);
internal sealed record UnaryExpr(string Op, Expr Value, int At) : Expr(At);
internal sealed record BinaryExpr(string Op, Expr Left, Expr Right, int At) : Expr(At);
internal sealed record IfExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse, int At) : Expr(At);
internal sealed record HistoryExpr(Expr Value, int Offset, int At) : Expr(At);
internal sealed record CallExpr(string Name, Expr[] Args, int At) : Expr(At);
internal sealed record Declaration(string Name, Expr Value, bool IsInput, bool IsPlot, int Line);
internal sealed record ScriptOrder(ScriptAction Action, Expr Condition, string Name, int Line);
internal sealed record Argument(string? Name, Expr Value);
internal sealed record Token(string Text, int Line, bool IsString = false);

internal sealed class ScriptParser
{
    private readonly List<Token> _tokens = [];
    private readonly List<ScriptDiagnostic> _diagnostics;
    private readonly Dictionary<string, Declaration> _declarations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScriptOrder> _orders = [];
    private readonly List<Expr> _decorations = [];
    private readonly List<Expr> _ignoredNumericArguments = [];
    private readonly List<(string Name, int Line)> _decoratedPlots = [];
    private int _index;
    private int _nodes;
    private int _depth;

    public ScriptParser(string source, List<ScriptDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
        Tokenize(source);
    }

    public ScriptProgram Parse()
    {
        while (_index < _tokens.Count - 1)
        {
            Token first = Take();
            if (first.IsString || !IsIdentifier(first.Text)) throw Error(first, "Expected a declaration or supported statement.");
            string keyword = first.Text.ToLowerInvariant();
            if (keyword == "declare")
            {
                Token declaration = Take();
                if (declaration.IsString || declaration.Text.ToLowerInvariant() is not ("upper" or "lower"))
                    throw Error(declaration, "Only 'declare upper' and 'declare lower' are supported.");
                Expect(";");
            }
            else if (keyword is "input" or "def" or "plot")
            {
                Token name = TakeIdentifier();
                if (ScriptProgram.IsBuiltin(name.Text) || name.Text.Contains('.'))
                    throw Error(name, "A declaration cannot replace a built-in name.");
                Expect("=");
                Expr value = Expression();
                Expect(";");
                if (!_declarations.TryAdd(name.Text, new(name.Text, value, keyword == "input", keyword == "plot", name.Line)))
                    throw Error(name, $"Duplicate declaration '{name.Text}'.");
            }
            else
            {
                Expect("(");
                List<Argument> arguments = Arguments();
                Expect(";");
                if (keyword == "addorder")
                {
                    AddOrder(first, arguments);
                }
                else
                {
                    AddDecoration(first, arguments);
                }
            }
        }
        foreach ((string name, int line) in _decoratedPlots)
        {
            if (!_declarations.TryGetValue(name, out Declaration? declaration) || !declaration.IsPlot)
                throw new ScriptException(line, $"Visual method target '{name}' is not a declared plot.");
        }
        if (_orders.Count == 0)
            throw new ScriptException(1, "This is a study without supported AddOrder actions; plots and candle colors never create trades.");
        return new(_declarations, _orders, _decorations, _ignoredNumericArguments);
    }

    private void AddOrder(Token token, List<Argument> arguments)
    {
        Expr[] args = Bind(arguments, ["type", "condition", "price", "tradesize", "tickcolor", "arrowcolor", "name"],
            [new NameExpr("OrderType.BUY_AUTO", token.Line), null, new HistoryExpr(new NameExpr("open", token.Line), -1, token.Line),
                new NumberExpr(1, token.Line), new NameExpr("Color.MAGENTA", token.Line), new NameExpr("Color.MAGENTA", token.Line), new TextExpr("Script signal", token.Line)], token);
        string type = args[0] is NameExpr name ? name.Name.ToLowerInvariant() : "";
        ScriptAction action = type switch
        {
            "ordertype.buy_to_open" or "ordertype.buy_auto" => ScriptAction.Buy,
            "ordertype.sell_to_close" or "ordertype.sell_auto" => ScriptAction.Sell,
            _ => throw Error(token, "Only BUY_TO_OPEN, SELL_TO_CLOSE, BUY_AUTO and SELL_AUTO are supported; short orders are prohibited."),
        };
        if (args[2] is not HistoryExpr { Offset: -1, Value: NameExpr { Name: var priceName } } ||
            !priceName.Equals("open", StringComparison.OrdinalIgnoreCase))
            _ignoredNumericArguments.Add(args[2]);
        _ignoredNumericArguments.Add(args[3]);
        _decorations.AddRange(args[4..]);
        string label = args[6] is TextExpr text ? text.Value : "Script signal";
        _orders.Add(new(action, args[1], label, token.Line));
        Warn(token.Line, "AddOrder proposes a long-only action after the completed bar. The host owns execution price and quantity; script price, tradeSize and decorations are ignored.");
    }

    private void AddDecoration(Token token, List<Argument> arguments)
    {
        string name = token.Text.ToLowerInvariant();
        int dot = name.LastIndexOf('.');
        string method = dot >= 0 ? name[(dot + 1)..] : name;
        string[] supported = dot >= 0
            ? ["setdefaultcolor", "assignvaluecolor", "setpaintingstrategy", "setlineweight", "setstyle", "hidebubble", "hidetitle", "hide", "sethiding"]
            : ["addlabel", "addcloud", "assignpricecolor", "addchartbubble"];
        if (!supported.Contains(method))
            throw Error(token, $"Unsupported statement '{token.Text}'. No external APIs, custom scripts, or order side effects are available.");
        if (dot >= 0) _decoratedPlots.Add((token.Text[..dot], token.Line));
        Expr Num(double value) => new NumberExpr(value, token.Line);
        Expr Color(string color) => new NameExpr("Color." + color, token.Line);
        (string[] Names, Expr?[] Defaults) signature = method switch
        {
            "hidebubble" or "hidetitle" or "hide" => ([], []),
            "setdefaultcolor" or "assignvaluecolor" or "assignpricecolor" => (["color"], [null]),
            "setpaintingstrategy" => (["paintingstrategy"], [null]),
            "setlineweight" => (["weight"], [null]),
            "setstyle" => (["curve"], [null]),
            "sethiding" => (["condition"], [null]),
            "addlabel" => (["visible", "text", "color"], [null, null, Color("RED")]),
            "addcloud" => (["data1", "data2", "color1", "color2", "showborder"], [null, null, Color("YELLOW"), Color("RED"), Num(0)]),
            "addchartbubble" => (["timecondition", "pricelocation", "text", "color", "up"], [null, null, null, Color("RED"), Num(1)]),
            _ => throw Error(token, "Unsupported visual annotation."),
        };
        _decorations.AddRange(Bind(arguments, signature.Names, signature.Defaults, token));
        Warn(token.Line, $"Visual annotation '{token.Text}' is validated but not rendered by this runtime.");
    }

    private Expr Expression(int minimumPrecedence = 0)
    {
        if (++_depth > 64) throw Error(Current, "Expression nesting exceeds 64 levels.");
        try
        {
            Token token = Take();
            Expr left;
            if (token.IsString) left = Node(new TextExpr(token.Text, token.Line));
            else if (token.Text.Equals("if", StringComparison.OrdinalIgnoreCase))
            {
                Expr condition = Expression();
                Expect("then");
                Expr whenTrue = Expression();
                Expect("else");
                left = Node(new IfExpr(condition, whenTrue, Expression(), token.Line));
            }
            else if (token.Text.ToLowerInvariant() is "+" or "-" or "!" or "not")
                left = Node(new UnaryExpr(token.Text.ToLowerInvariant(), Expression(7), token.Line));
            else if (token.Text == "(")
            {
                left = Expression();
                Expect(")");
            }
            else if (double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                if (!double.IsFinite(number)) throw Error(token, "Numeric literal must be finite.");
                left = Node(new NumberExpr(number, token.Line));
            }
            else if (IsIdentifier(token.Text))
            {
                if (Match("(")) left = Function(token, Arguments());
                else left = Node(new NameExpr(token.Text, token.Line));
            }
            else throw Error(token, $"Expected an expression, found '{token.Text}'.");

            while (true)
            {
                if (Match("["))
                {
                    int sign = Match("-") ? -1 : 1;
                    Token offset = Take();
                    if (offset.IsString || !int.TryParse(offset.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > ThinkScriptCompiler.MaximumPeriod)
                        throw Error(offset, "History offset must be a constant integer between 0 and 2048.");
                    Expect("]");
                    left = Node(new HistoryExpr(left, count * sign, offset.Line));
                    continue;
                }
                if (Match("."))
                {
                    Token member = TakeIdentifier();
                    if (left is not CallExpr { Name: "rsi" } || !member.Text.Equals("RSI", StringComparison.OrdinalIgnoreCase))
                        throw Error(member, "Only the RSI().RSI plot accessor is supported.");
                    continue;
                }
                string op = Current.Text.ToLowerInvariant();
                if (Current.IsString) break;
                int precedence = op switch
                {
                    "or" or "||" => 1, "and" or "&&" => 2,
                    "==" or "!=" or "<>" or "is" => 3,
                    ">" or "<" or ">=" or "<=" or "crosses" => 4,
                    "+" or "-" => 5, "*" or "/" or "%" => 6, _ => -1,
                };
                if (precedence < minimumPrecedence) break;
                Take();
                if (op == "is") op = Match("not") ? "!=" : "==";
                if (op == "crosses") op = Match("above") ? "crossabove" : Match("below") ? "crossbelow" : "crossany";
                left = Node(new BinaryExpr(op, left, Expression(precedence + 1), token.Line));
            }
            return left;
        }
        finally { _depth--; }
    }

    private Expr Function(Token token, List<Argument> arguments)
    {
        string name = token.Text.ToLowerInvariant();
        Expr Num(double value) => new NumberExpr(value, token.Line);
        Expr Close() => new NameExpr("close", token.Line);
        (string[] Names, Expr?[] Defaults) signature = name switch
        {
            "average" or "expaverage" or "wildersaverage" or "highest" or "lowest" or "sum" => (["data", "length"], [null, Num(12)]),
            "rsi" => (["length", "over_bought", "over_sold", "price", "averagetype", "showbreakoutsignals"], [Num(14), Num(70), Num(30), Close(), new NameExpr("AverageType.WILDERS", token.Line), Num(0)]),
            "movingaverage" => (["averagetype", "data", "length"], [null, null, Num(12)]),
            "min" or "max" or "power" => (["value1", "value2"], [null, null]),
            "absvalue" or "sqrt" or "sqr" or "sign" or "isnan" => (["value"], [null]),
            "round" => (["number", "numberofdigits"], [null, Num(2)]),
            "truerange" => (["high", "close", "low"], [new NameExpr("high", token.Line), Close(), new NameExpr("low", token.Line)]),
            "crosses" => (["data1", "data2", "direction"], [null, null, new NameExpr("CrossingDirection.ANY", token.Line)]),
            "countsince" => (["condition", "reset"], [null, null]),
            "open" or "high" or "low" or "close" or "hl2" or "hlc3" or "ohlc4" => ([], []),
            _ => throw Error(token, $"Unsupported function '{token.Text}'. Secondary symbols/timeframes, volume studies and external APIs are unavailable."),
        };
        Expr[] args = Bind(arguments, signature.Names, signature.Defaults, token);
        if (args.Length == 0) return Node(new NameExpr(name, token.Line));
        if (name is "expaverage" or "wildersaverage" or "rsi" or "movingaverage")
            Warn(token.Line, "Smoothing uses deterministic supplied-history seeds and explicit warmup; thinkorswim prefetch outside the supplied history is unavailable.");
        if (name == "countsince")
            Warn(token.Line, "CountSince is a PriceSentinel extension, not a native thinkScript function. Counts reset on missing observations and are retained within the running session.");
        return Node(new CallExpr(name, args, token.Line));
    }

    private static Expr[] Bind(List<Argument> arguments, string[] names, Expr?[] defaults, Token token)
    {
        Expr?[] values = [.. defaults];
        var assigned = new HashSet<int>();
        bool named = false;
        for (int i = 0; i < arguments.Count; i++)
        {
            Argument argument = arguments[i];
            int index = argument.Name is null ? i : Array.FindIndex(names, name => name.Equals(argument.Name, StringComparison.OrdinalIgnoreCase));
            if (argument.Name is null && named) throw Error(token, "Positional arguments must precede named arguments.");
            named |= argument.Name is not null;
            if (index < 0 || index >= values.Length || !assigned.Add(index))
                throw Error(token, $"Unknown, duplicate, or excessive argument in '{token.Text}'.");
            values[index] = argument.Value;
        }
        for (int i = 0; i < values.Length; i++)
            if (values[i] is null) throw Error(token, $"Missing '{names[i]}' argument for '{token.Text}'.");
        return values.Select(value => value!).ToArray();
    }

    private List<Argument> Arguments()
    {
        var args = new List<Argument>();
        if (Match(")")) return args;
        do
        {
            string? name = null;
            if (_index + 1 < _tokens.Count && _tokens[_index + 1].Text == "=")
            {
                name = TakeIdentifier().Text;
                Expect("=");
            }
            args.Add(new(name, Expression()));
            if (args.Count > 16) throw Error(Current, "A call cannot exceed 16 arguments.");
        } while (Match(","));
        Expect(")");
        return args;
    }

    private T Node<T>(T expression) where T : Expr
    {
        if (++_nodes > ThinkScriptCompiler.MaximumNodes) throw new ScriptException(expression.Line, "Script exceeds the 4096-node limit.");
        return expression;
    }
    private Token Current => _tokens[_index];
    private Token Take() { Token result = Current; if (_index < _tokens.Count - 1) _index++; return result; }
    private bool Match(string text) { if (!Current.IsString && Current.Text.Equals(text, StringComparison.OrdinalIgnoreCase)) { Take(); return true; } return false; }
    private void Expect(string text) { if (!Match(text)) throw Error(Current, $"Expected '{text}', found '{Current.Text}'."); }
    private Token TakeIdentifier() { Token token = Take(); if (token.IsString || !IsIdentifier(token.Text)) throw Error(token, "Expected an identifier."); return token; }
    private static bool IsIdentifier(string value) => value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_');
    private static ScriptException Error(Token token, string message) => new(token.Line, message);
    private void Warn(int line, string message) => _diagnostics.Add(new(line, message, false));

    private void Tokenize(string source)
    {
        int line = 1;
        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { if (c == '\n') line++; i++; continue; }
            if (c == '#' || c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            { while (i < source.Length && source[i] != '\n') i++; continue; }
            int start = i;
            bool isString = c == '"';
            string text;
            if (isString)
            {
                i++;
                var value = new System.Text.StringBuilder();
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\n') throw new ScriptException(line, "String literals must remain on one line.");
                    if (source[i] == '\\' && i + 1 < source.Length && source[i + 1] is '"' or '\\') i++;
                    value.Append(source[i++]);
                }
                if (i == source.Length) throw new ScriptException(line, "Unterminated string literal.");
                i++;
                text = value.ToString();
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                i++;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.' && i + 1 < source.Length && char.IsAsciiLetter(source[i + 1]))) i++;
                text = source[start..i];
            }
            else if (char.IsAsciiDigit(c) || c == '.' && i + 1 < source.Length && char.IsAsciiDigit(source[i + 1]))
            {
                i++;
                while (i < source.Length && (char.IsAsciiDigit(source[i]) || source[i] == '.')) i++;
                if (i < source.Length && source[i] is 'e' or 'E')
                {
                    i++;
                    if (i < source.Length && source[i] is '+' or '-') i++;
                    while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
                }
                text = source[start..i];
            }
            else if ("+-*/%=!<>|&()[];,.".Contains(c))
            {
                i++;
                if (i < source.Length && new[] { "==", "!=", ">=", "<=", "<>", "&&", "||" }.Contains(source[start..(i + 1)])) i++;
                text = source[start..i];
            }
            else throw new ScriptException(line, $"Unsupported character '{c}'.");
            _tokens.Add(new(text, line, isString));
            if (_tokens.Count > 24_000) throw new ScriptException(line, "Script exceeds the token limit.");
        }
        _tokens.Add(new("<end>", line));
    }
}
