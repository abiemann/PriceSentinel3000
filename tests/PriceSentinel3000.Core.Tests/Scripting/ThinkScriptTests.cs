using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.Scripting;

public sealed class ThinkScriptTests
{
    private static readonly StrategyPositionContext Held = new(1, 100, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ExplicitOrdersAreLongOnlyAndIgnoreExecutionHintsWithWarnings()
    {
        CompiledThinkScript script = Compile("""
            declare upper;
            input level = 100;
            plot baseline = level;
            baseline.SetDefaultColor(Color.GREEN);
            AddOrder(OrderType.BUY_AUTO, close > level, open[-1], 999999, name = "if");
            AddOrder(OrderType.SELL_AUTO, close < level, name = "Exit");
            """);
        Assert.Equal(ScriptAction.Buy, script.Evaluate(Bars(101), StrategyPositionContext.Flat).Action);
        Assert.Equal("if", script.Evaluate(Bars(101), StrategyPositionContext.Flat).Reason);
        Assert.Equal(ScriptAction.Hold, script.Evaluate(Bars(101), Held).Action);
        Assert.Equal(ScriptAction.Sell, script.Evaluate(Bars(99), Held).Action);
        Assert.Equal(ScriptAction.Hold, script.Evaluate(Bars(99), StrategyPositionContext.Flat).Action);
        Assert.Equal(100m, script.Evaluate(Bars(101), Held).Plots["baseline"]);
        Assert.Contains(script.Diagnostics, item => !item.IsError && item.Message.Contains("quantity"));
        Assert.Contains(script.Diagnostics, item => !item.IsError && item.Message.Contains("not rendered"));
    }

    [Fact]
    public void ConflictingSignalsFailClosed()
    {
        CompiledThinkScript script = Compile("AddOrder(OrderType.BUY_AUTO, yes); AddOrder(OrderType.SELL_AUTO, yes);");
        ScriptProposal proposal = script.Evaluate(Bars(100), Held);
        Assert.Equal(ScriptAction.Hold, proposal.Action);
        Assert.Equal("CONFLICTING SIGNALS", proposal.State);
    }

    [Fact]
    public void MovingAverageCrossUsesPreviousCompletedBarAndNamedInputs()
    {
        CompiledThinkScript script = Compile("""
            input Length = 3;
            input price = close;
            def averagePrice = Average(length = length, data = price);
            plot value = averagePrice;
            AddOrder(OrderType.BUY_TO_OPEN, close crosses above averagePrice);
            AddOrder(OrderType.SELL_TO_CLOSE, Crosses(close, averagePrice, CrossingDirection.BELOW));
            """);
        Assert.Equal(4, script.RequiredWarmupBars);
        Assert.Equal(3, script.DefaultInputs["length"]);
        Assert.False(script.DefaultInputs.ContainsKey("price"));
        Assert.Equal("WARMING UP", script.Evaluate(Bars(10, 10, 9), StrategyPositionContext.Flat).State);
        ScriptProposal proposal = script.Evaluate(Bars(10, 10, 9, 12), StrategyPositionContext.Flat);
        Assert.Equal(ScriptAction.Buy, proposal.Action);
        Assert.Equal(31m / 3, proposal.Plots["value"]!.Value, 10);
        Assert.Equal(ScriptAction.Sell, script.Evaluate(Bars(10, 10, 12, 9), Held).Action);
    }

    [Fact]
    public void HistoryOfFunctionResultsAndSimpleIndicatorsHaveExpectedValues()
    {
        CompiledThinkScript script = Compile("""
            plot priorLow = Lowest(close, 3)[1];
            plot peak = Highest(close, 4);
            plot total = Sum(close, 3);
            plot range = TrueRange();
            plot mid = hl2();
            plot math = Round(AbsValue(-2.555), 2) + Min(2, 3) + Max(2, 3) + Sqr(2) + Sqrt(4) + Power(2, 3);
            AddOrder(OrderType.BUY_TO_OPEN, no);
            """);
        ScriptProposal proposal = script.Evaluate(Bars(10, 9, 12, 11), Held);
        Assert.Equal(9, proposal.Plots["priorLow"]);
        Assert.Equal(12, proposal.Plots["peak"]);
        Assert.Equal(32, proposal.Plots["total"]);
        Assert.Equal(1, proposal.Plots["range"]);
        Assert.Equal(11, proposal.Plots["mid"]);
        Assert.Equal(21.56m, proposal.Plots["math"]);
    }

    [Theory]
    [InlineData("Double.NaN")]
    [InlineData("1 / 0")]
    [InlineData("Sqrt(-1)")]
    [InlineData("Double.NaN != 1")]
    [InlineData("not Double.NaN")]
    [InlineData("if Double.NaN then yes else yes")]
    [InlineData("Double.NaN and yes")]
    public void NonFiniteConditionsNeverBecomeAnOrder(string condition)
    {
        CompiledThinkScript script = Compile($"plot value = {condition}; AddOrder(OrderType.BUY_AUTO, value);");
        ScriptProposal proposal = script.Evaluate(Bars(10), StrategyPositionContext.Flat);
        Assert.Equal(ScriptAction.Hold, proposal.Action);
        Assert.Null(proposal.Plots["value"]);
    }

    [Fact]
    public void ExplicitIsNaNGuardWorksWithoutMakingNaNTruthy()
    {
        CompiledThinkScript script = Compile("AddOrder(OrderType.BUY_AUTO, if IsNaN(1 / 0) then yes else no);");
        Assert.Equal(ScriptAction.Buy, script.Evaluate(Bars(10), StrategyPositionContext.Flat).Action);
    }

    [Fact]
    public void SmoothingUsesDeterministicSeedsAndRequiresPrefetchWarmup()
    {
        CompiledThinkScript script = Compile("""
            plot exponential = ExpAverage(close, 2);
            plot wilder = WildersAverage(close, 2);
            plot simple = MovingAverage(AverageType.SIMPLE, close, 2);
            AddOrder(OrderType.BUY_AUTO, no);
            """);
        Assert.Equal(14, script.RequiredWarmupBars);
        decimal[] prices = Enumerable.Range(1, 14).Select(i => (decimal)i).ToArray();
        ScriptProposal proposal = script.Evaluate(Bars(prices), StrategyPositionContext.Flat);
        double ema = 1;
        double wilder = 1.5;
        for (int i = 2; i <= 14; i++) ema = 2d / 3 * i + ema / 3;
        for (int i = 3; i <= 14; i++) wilder = 0.5 * i + 0.5 * wilder;
        Assert.Equal((decimal)ema, proposal.Plots["exponential"]!.Value, 10);
        Assert.Equal((decimal)wilder, proposal.Plots["wilder"]!.Value, 10);
        Assert.Equal(13.5m, proposal.Plots["simple"]);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(-1, 0)]
    [InlineData(0, 50)]
    public void RsiHandlesRisingFallingAndFlatPrices(int direction, int expected)
    {
        CompiledThinkScript script = Compile("plot momentum = RSI(length = 2, price = close).RSI; AddOrder(OrderType.BUY_AUTO, no);");
        Assert.Equal(15, script.RequiredWarmupBars);
        decimal[] prices = Enumerable.Range(0, 15).Select(i => 100m + i * direction).ToArray();
        Assert.Equal(expected, script.Evaluate(Bars(prices), Held).Plots["momentum"]);
    }

    [Fact]
    public void WilderRsiMatchesIndependentGainLossRecurrence()
    {
        decimal[] prices = [10, 12, 11, 13, 12, 15, 14, 16, 13, 12, 14, 17, 16, 15, 18];
        CompiledThinkScript script = Compile("plot momentum = RSI(length = 2); AddOrder(OrderType.BUY_AUTO, no);");
        double gain = 1, loss = 0.5;
        for (int i = 3; i < prices.Length; i++)
        {
            double change = (double)(prices[i] - prices[i - 1]);
            gain = (gain + Math.Max(change, 0)) / 2;
            loss = (loss + Math.Max(-change, 0)) / 2;
        }
        Assert.Equal((decimal)(100 - 100 / (1 + gain / loss)), script.Evaluate(Bars(prices), Held).Plots["momentum"]!.Value, 10);
    }

    [Theory]
    [InlineData("plot Buy = close > close[1];", "study")]
    [InlineData("def v = volume; AddOrder(OrderType.BUY_AUTO, v > 0);", "Volume")]
    [InlineData("AddOrder(OrderType.SELL_TO_OPEN, yes);", "short")]
    [InlineData("AddOrder(OrderType.BUY_TO_CLOSE, yes);", "short")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, open[-1] > close);", "Future")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, yes, close[-1]);", "Future")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, yes, open[-1] + 0);", "Future")]
    [InlineData("def future = open[-1]; AddOrder(OrderType.BUY_AUTO, yes, future);", "Future")]
    [InlineData("AddLabel(yes, close[-1], Color.RED); AddOrder(OrderType.BUY_AUTO, no);", "Future")]
    [InlineData("def a = a[1] + 1; AddOrder(OrderType.BUY_AUTO, a > 2);", "recursive")]
    [InlineData("def a = b; def b = a; AddOrder(OrderType.BUY_AUTO, yes);", "recursive")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, System.IO.File.ReadAllText(\"x\"));", "Unsupported function")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, close(period = AggregationPeriod.DAY) > 0);", "argument")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, unknown > 1);", "identifier")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, Average(close, close) > 1);", "constant integer")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, Average(close, 0) > 1);", "constant integer")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, Average(close, 2049) > 1);", "constant integer")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, RSI(length = 1000) > 50);", "warmup")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, no, strange = yes);", "argument")]
    [InlineData("AddLabel(); AddOrder(OrderType.BUY_AUTO, no);", "Missing")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, \"yes\");", "Strings")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, yes, \"invalid price\");", "Strings")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, yes, open[-1], \"invalid size\");", "Strings")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, close \"and\" yes);", "Expected")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, yes); \"<end>\" AddOrder(OrderType.SELL_TO_OPEN, yes);", "Expected")]
    [InlineData("declare \"upper\"; AddOrder(OrderType.BUY_AUTO, yes);", "declare")]
    [InlineData("AddOrder(OrderType.BUY_AUTO, close[\"1\"] > 0);", "History offset")]
    public void IncompatibleProgramsReturnActionableDiagnostics(string source, string expected)
    {
        CompiledThinkScript script = ThinkScriptCompiler.Compile("# Heading\n" + source);
        Assert.False(script.IsCompatible);
        Assert.Contains(script.Diagnostics, item => item.IsError && item.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.All(script.Diagnostics, item => Assert.True(item.Line >= 1));
        Assert.Equal(ScriptAction.Hold, script.Evaluate(Bars(10), Held).Action);
    }

    [Fact]
    public void InvalidInputOverrideFailsClosedWithoutChangingCompiledDefaults()
    {
        CompiledThinkScript script = Compile("input length = 2; AddOrder(OrderType.BUY_AUTO, close > Average(close, length));");
        Assert.Equal(ScriptAction.Buy, script.Evaluate(Bars(10, 11), StrategyPositionContext.Flat).Action);
        Assert.Equal("SCRIPT ERROR", script.Evaluate(Bars(10, 11), StrategyPositionContext.Flat, new Dictionary<string, decimal> { ["length"] = 0 }).State);
        Assert.Equal("SCRIPT ERROR", script.Evaluate(Bars(10, 11), StrategyPositionContext.Flat, new Dictionary<string, decimal> { ["unknown"] = 2 }).State);
        Assert.Equal("WARMING UP", script.Evaluate(Bars(10, 11), StrategyPositionContext.Flat, new Dictionary<string, decimal> { ["LENGTH"] = 3 }).State);
        Assert.Equal(2, script.DefaultInputs["length"]);
        Assert.Equal(2, script.RequiredWarmupBars);
    }

    [Fact]
    public void InvalidBarsAndTooMuchHistoryFailClosed()
    {
        CompiledThinkScript script = Compile("AddOrder(OrderType.BUY_AUTO, yes);");
        StrategyBar bar = Bars(10)[0];
        Assert.Equal("SCRIPT ERROR", script.Evaluate([bar with { High = 9 }], Held).State);
        Assert.Equal("SCRIPT ERROR", script.Evaluate([bar, bar], Held).State);
        Assert.Equal("SCRIPT ERROR", script.Evaluate([bar with { Close = 0 }], Held).State);
        Assert.Equal("SCRIPT ERROR", script.Evaluate(Bars(Enumerable.Repeat(10m, 4097).ToArray()), Held).State);
        Assert.Equal("WARMING UP", script.Evaluate([], Held).State);
    }

    [Fact]
    public void SourceAstAndDependencyLimitsAreEnforcedWithoutRecursingUnboundedly()
    {
        Assert.False(ThinkScriptCompiler.Compile(new string(' ', ThinkScriptCompiler.MaximumSourceLength + 1)).IsCompatible);
        string deeplyNested = new string('(', 100) + "yes" + new string(')', 100);
        Assert.False(ThinkScriptCompiler.Compile($"AddOrder(OrderType.BUY_AUTO, {deeplyNested});").IsCompatible);
        string chain = "def v0 = 1;" + string.Concat(Enumerable.Range(1, 100).Select(i => $"def v{i} = v{i - 1} + v{i - 1};"));
        Assert.False(ThinkScriptCompiler.Compile(chain + "AddOrder(OrderType.BUY_AUTO, yes);").IsCompatible);
        string tooManyNodes = string.Concat(Enumerable.Range(0, 4200).Select(i => $"def v{i} = 1;"));
        Assert.False(ThinkScriptCompiler.Compile(tooManyNodes + "AddOrder(OrderType.BUY_AUTO, yes);").IsCompatible);
    }

    [Fact]
    public void EvaluationBudgetsFailClosed()
    {
        string manyPlots = string.Concat(Enumerable.Range(0, 600).Select(i => $"plot v{i} = close + {i};"));
        CompiledThinkScript memory = Compile(manyPlots + "AddOrder(OrderType.BUY_AUTO, yes);");
        ScriptProposal memoryResult = memory.Evaluate(Bars(Enumerable.Repeat(10m, 4096).ToArray()), Held);
        Assert.Equal("SCRIPT ERROR", memoryResult.State);
        Assert.Contains("memory budget", memoryResult.Reason);

        CompiledThinkScript expensive = Compile("plot value = Highest(close, 2048); AddOrder(OrderType.BUY_AUTO, yes);");
        ScriptProposal operationResult = expensive.Evaluate(Bars(Enumerable.Repeat(10m, 4096).ToArray()), Held);
        Assert.Equal("SCRIPT ERROR", operationResult.State);
        Assert.Contains("operation budget", operationResult.Reason);
    }

    private static CompiledThinkScript Compile(string source)
    {
        CompiledThinkScript script = ThinkScriptCompiler.Compile(source);
        Assert.True(script.IsCompatible, string.Join("; ", script.Diagnostics.Where(item => item.IsError).Select(item => $"Line {item.Line}: {item.Message}")));
        return script;
    }
    private static StrategyBar[] Bars(params decimal[] closes) => closes.Select((price, index) =>
        new StrategyBar(DateTimeOffset.UnixEpoch.AddMinutes(index), DateTimeOffset.UnixEpoch.AddMinutes(index + 1), price, price, price, price, 0)).ToArray();
}
