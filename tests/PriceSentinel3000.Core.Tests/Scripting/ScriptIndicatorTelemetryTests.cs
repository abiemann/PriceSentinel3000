using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.Scripting;

public sealed class ScriptIndicatorTelemetryTests
{
    [Fact]
    public void WarmupReportsNullValuesThenUsesTheActualCachedDeclarations()
    {
        CompiledThinkScript script = Compile("""
            input length = 2;
            def averagePrice = Average(close, length);
            def entry = close > averagePrice;
            def unused = 42;
            plot displayed = averagePrice;
            AddOrder(OrderType.BUY_TO_OPEN, entry);
            """);

        ScriptProposal warmup = script.Evaluate(Bars(10), StrategyPositionContext.Flat);
        Assert.Equal("WARMING UP", warmup.State);
        Assert.Equal(4, warmup.IndicatorCount);
        Assert.All(warmup.Indicators, value =>
        {
            Assert.Null(value.Value);
            Assert.Equal("warming_up", value.State);
        });

        ScriptProposal evaluated = script.Evaluate(Bars(10, 12), StrategyPositionContext.Flat);
        Assert.Equal(ScriptAction.Buy, evaluated.Action);
        Assert.Contains(new("averagePrice", "def", 11, "available"), evaluated.Indicators);
        Assert.Contains(new("entry", "def", 1, "available"), evaluated.Indicators);
        Assert.Contains(new("unused", "def", null, "not_evaluated"), evaluated.Indicators);
        Assert.Contains(new("displayed", "plot", evaluated.Plots["displayed"], "available"), evaluated.Indicators);
        Assert.DoesNotContain(evaluated.Indicators, value => value.Name == "length");
        Assert.False(evaluated.IndicatorsTruncated);
    }

    [Fact]
    public void UndefinedValuesAreDistinctFromUnusedDeclarations()
    {
        CompiledThinkScript script = Compile("""
            def invalid = 1 / 0;
            def unused = 2;
            AddOrder(OrderType.BUY_TO_OPEN, invalid);
            """);

        ScriptProposal result = script.Evaluate(Bars(10), StrategyPositionContext.Flat);

        Assert.Equal(ScriptAction.Hold, result.Action);
        Assert.Contains(new("invalid", "def", null, "unavailable"), result.Indicators);
        Assert.Contains(new("unused", "def", null, "not_evaluated"), result.Indicators);
    }

    [Fact]
    public void TelemetryIsBoundedAndNeverEvaluatesExpensiveUnusedDeclarations()
    {
        string declarations = string.Concat(Enumerable.Range(0, 70).Select(index =>
            $"def unused{index} = Highest(close, 2048);"));
        CompiledThinkScript script = Compile(declarations + "AddOrder(OrderType.BUY_TO_OPEN, yes);");

        ScriptProposal result = script.Evaluate(Bars(Enumerable.Repeat(10m, 4096).ToArray()), StrategyPositionContext.Flat);

        // Evaluating even one unused Highest expression on this history would
        // exceed the operation budget and replace this buy with SCRIPT ERROR.
        Assert.Equal(ScriptAction.Buy, result.Action);
        Assert.Equal(70, result.IndicatorCount);
        Assert.Equal(64, result.IndicatorLimit);
        Assert.Equal(result.IndicatorLimit, result.Indicators.Count);
        Assert.True(result.IndicatorsTruncated);
        Assert.All(result.Indicators, value => Assert.Equal("not_evaluated", value.State));
    }

    [Fact]
    public void RuntimeFailureExposesOnlySuccessfullyCachedValues()
    {
        CompiledThinkScript script = Compile("""
            plot cached = close;
            plot expensive = Highest(close, 2048);
            AddOrder(OrderType.BUY_TO_OPEN, yes);
            """);

        ScriptProposal result = script.Evaluate(Bars(Enumerable.Repeat(10m, 4096).ToArray()), StrategyPositionContext.Flat);

        Assert.Equal("SCRIPT ERROR", result.State);
        Assert.Contains("operation budget", result.Reason);
        Assert.Contains(new("cached", "plot", 10, "available"), result.Indicators);
        Assert.Contains(new("expensive", "plot", null, "not_evaluated"), result.Indicators);
        Assert.Equal(ScriptAction.Hold, result.Action);
    }

    private static CompiledThinkScript Compile(string source)
    {
        CompiledThinkScript script = ThinkScriptCompiler.Compile(source);
        Assert.True(script.IsCompatible);
        return script;
    }

    private static StrategyBar[] Bars(params decimal[] prices) => prices.Select((price, index) =>
        new StrategyBar(DateTimeOffset.UnixEpoch.AddMinutes(index), DateTimeOffset.UnixEpoch.AddMinutes(index + 1),
            price, price, price, price, 0)).ToArray();
}
