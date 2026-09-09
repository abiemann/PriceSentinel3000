using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.Scripting;

public sealed class CountSinceTests
{
    [Fact]
    public void CountsEventsAndResetsBeforeCountingTheResetBar()
    {
        CompiledThinkScript script = Compile("plot count = CountSince(condition = close < 30, reset = close >= 60); AddOrder(OrderType.BUY_TO_OPEN, no);");
        StrategyBar[] bars = Bars(20, 40, 20, 60, 20, 40, 20, 20);
        decimal[] expected = [1, 1, 2, 0, 1, 1, 2, 3];
        for (int i = 1; i <= bars.Length; i++)
            Assert.Equal(expected[i - 1], Count(script.Evaluate(bars.Take(i).ToArray(), StrategyPositionContext.Flat)));
        CompiledThinkScript both = Compile("plot count = CountSince(yes, yes); AddOrder(OrderType.BUY_TO_OPEN, no);");
        Assert.Equal(0, Count(both.Evaluate(bars, StrategyPositionContext.Flat)));
        Assert.Contains(script.Diagnostics, diagnostic => !diagnostic.IsError && diagnostic.Message.Contains("PriceSentinel extension"));
    }

    [Fact]
    public void MissingArgumentsClearTheCounterUntilValuesAreKnownAndResetHasPriority()
    {
        CompiledThinkScript script = Compile("plot count = CountSince(if close == 50 then Double.NaN else yes, if close == 40 then Double.NaN else no); AddOrder(OrderType.BUY_TO_OPEN, no);");
        StrategyBar[] bars = Bars(20, 20, 50, 20, 40, 20);
        decimal?[] expected = [1, 2, null, 1, null, 1];
        for (int i = 1; i <= bars.Length; i++)
            Assert.Equal(expected[i - 1], Count(script.Evaluate(bars.Take(i).ToArray(), StrategyPositionContext.Flat)));
        CompiledThinkScript reset = Compile("plot count = CountSince(Double.NaN, yes); AddOrder(OrderType.BUY_TO_OPEN, no);");
        Assert.Equal(0, Count(reset.Evaluate(bars, StrategyPositionContext.Flat)));
    }

    [Fact]
    public void RollingSessionHistoryPreservesCountsAndOffsetsWithoutDoubleCounting()
    {
        CompiledThinkScript script = Compile("plot count = CountSince(yes, no); plot prior = count[1]; AddOrder(OrderType.BUY_TO_OPEN, no);");
        var state = new ScriptEvaluationState();
        StrategyBar[] all = Bars(Enumerable.Repeat(20m, 700).ToArray());
        for (int length = 2; length <= all.Length; length++)
        {
            StrategyBar[] window = all.Take(length).TakeLast(256).ToArray();
            ScriptProposal first = script.Evaluate(window, StrategyPositionContext.Flat, state: state);
            ScriptProposal repeated = script.Evaluate(window, StrategyPositionContext.Flat, state: state);
            Assert.Equal(length, Count(first));
            Assert.Equal(length - 1, first.Plots["prior"]);
            Assert.Equal(Count(first), Count(repeated));
        }
        Assert.Equal(256, Count(script.Evaluate(all.TakeLast(256).ToArray(), StrategyPositionContext.Flat)));
    }

    [Fact]
    public void GapsRewindsChangedInputsAndProgramsDoNotLeakCounts()
    {
        CompiledThinkScript script = Compile("input increment = 1; plot count = CountSince(increment, no); AddOrder(OrderType.BUY_TO_OPEN, no);");
        var state = new ScriptEvaluationState();
        StrategyBar[] all = Bars(Enumerable.Repeat(20m, 300).ToArray());
        Assert.Equal(256, Count(script.Evaluate(all.Take(256).ToArray(), StrategyPositionContext.Flat, state: state)));
        Assert.Equal(257, Count(script.Evaluate(all.Skip(1).Take(256).ToArray(), StrategyPositionContext.Flat, state: state)));
        Assert.Equal(0, Count(script.Evaluate(all.Skip(2).Take(256).ToArray(), StrategyPositionContext.Flat,
            new Dictionary<string, decimal> { ["increment"] = 0 }, state)));
        Assert.Equal(256, Count(script.Evaluate(all.Skip(3).Take(256).ToArray(), StrategyPositionContext.Flat, state: state)));
        Assert.Equal(3, Count(script.Evaluate(all.Take(3).ToArray(), StrategyPositionContext.Flat, state: state)));
        Assert.Equal(1, Count(script.Evaluate([all[9]], StrategyPositionContext.Flat, state: state)));
        Assert.Equal(1, Count(script.Evaluate([all[9] with { Close = 21, High = 21 }], StrategyPositionContext.Flat, state: state)));
        CompiledThinkScript other = Compile("plot count = CountSince(yes, no); AddOrder(OrderType.BUY_TO_OPEN, no);");
        Assert.Equal(1, Count(other.Evaluate([all[10]], StrategyPositionContext.Flat, state: state)));
        Assert.Equal(1, Count(other.Evaluate([all[0], all[1], all[9]], StrategyPositionContext.Flat)));
        state.Reset();
        Assert.Equal(1, Count(other.Evaluate([all[11]], StrategyPositionContext.Flat, state: state)));
    }

    [Fact]
    public void SuccessfulUnrelatedProgramAndRemovedHistoryBreakContinuity()
    {
        CompiledThinkScript counter = Compile("plot count = CountSince(yes, no); AddOrder(OrderType.BUY_TO_OPEN, no);");
        CompiledThinkScript unrelated = Compile("AddOrder(OrderType.BUY_TO_OPEN, no);");
        StrategyBar[] bars = Bars(20, 20, 20, 20);
        var state = new ScriptEvaluationState();
        Assert.Equal(3, Count(counter.Evaluate(bars.Take(3).ToArray(), StrategyPositionContext.Flat, state: state)));
        Assert.Equal(1, Count(counter.Evaluate([bars[0], bars[2]], StrategyPositionContext.Flat, state: state)));
        counter.Evaluate(bars.Take(3).ToArray(), StrategyPositionContext.Flat, state: state);
        unrelated.Evaluate(bars.Take(3).ToArray(), StrategyPositionContext.Flat, state: state);
        Assert.Equal(3, Count(counter.Evaluate(bars.Skip(1).ToArray(), StrategyPositionContext.Flat, state: state)));
    }

    [Fact]
    public void FailedEvaluationCannotReplaceTheLastSuccessfulCheckpoint()
    {
        CompiledThinkScript script = Compile("plot count = CountSince(yes, no); AddOrder(OrderType.BUY_TO_OPEN, no);");
        CompiledThinkScript expensive = Compile("plot count = CountSince(yes, no); plot costly = Highest(close, 2048); AddOrder(OrderType.BUY_TO_OPEN, no);");
        StrategyBar[] all = Bars(Enumerable.Repeat(20m, 4096).ToArray());
        var state = new ScriptEvaluationState();
        Assert.Equal(256, Count(script.Evaluate(all.Take(256).ToArray(), StrategyPositionContext.Flat, state: state)));
        ScriptProposal failed = expensive.Evaluate(all, StrategyPositionContext.Flat, state: state);
        Assert.Equal("SCRIPT ERROR", failed.State);
        Assert.Contains("operation budget", failed.Reason);
        Assert.Equal(257, Count(script.Evaluate(all.Skip(1).Take(256).ToArray(), StrategyPositionContext.Flat, state: state)));
    }

    [Fact]
    public void AddedOlderHistoryRecomputesTheCounter()
    {
        CompiledThinkScript script = Compile("plot count = CountSince(yes, no); AddOrder(OrderType.BUY_TO_OPEN, no);");
        StrategyBar[] bars = Bars(20, 20, 20, 20);
        var state = new ScriptEvaluationState();
        Assert.Equal(2, Count(script.Evaluate(bars.Skip(2).ToArray(), StrategyPositionContext.Flat, state: state)));
        Assert.Equal(4, Count(script.Evaluate(bars, StrategyPositionContext.Flat, state: state)));
    }

    [Fact]
    public void CounterStateRemainsSubjectToTheRuntimeMemoryBudget()
    {
        string plots = string.Join("\n", Enumerable.Range(0, 170).Select(index => $"plot count{index} = CountSince(yes, no);"));
        CompiledThinkScript script = Compile(plots + " AddOrder(OrderType.BUY_TO_OPEN, no);");
        ScriptProposal result = script.Evaluate(Bars(Enumerable.Repeat(20m, 4096).ToArray()),
            StrategyPositionContext.Flat, state: new ScriptEvaluationState());
        Assert.Equal("SCRIPT ERROR", result.State);
        Assert.Contains("memory budget", result.Reason);
    }
    [Theory]
    [InlineData("CountSince(close[-1] > 30, no)")]
    [InlineData("CountSince(yes)")]
    [InlineData("CountSince(yes, no, yes)")]
    [InlineData("CountSince(condition = yes, reset = no, condition = no)")]
    public void InvalidAndFutureReferencesAreRejected(string expression) =>
        Assert.False(ThinkScriptCompiler.Compile($"plot count = {expression}; AddOrder(OrderType.BUY_TO_OPEN, no);").IsCompatible);

    private static decimal? Count(ScriptProposal proposal)
    {
        Assert.NotEqual("SCRIPT ERROR", proposal.State);
        return proposal.Plots["count"];
    }

    private static CompiledThinkScript Compile(string source)
    {
        CompiledThinkScript script = ThinkScriptCompiler.Compile(source);
        Assert.True(script.IsCompatible, string.Join("; ", script.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return script;
    }

    private static StrategyBar[] Bars(params decimal[] prices) => prices.Select((price, index) =>
        new StrategyBar(DateTimeOffset.UnixEpoch.AddSeconds(index * 15), DateTimeOffset.UnixEpoch.AddSeconds((index + 1) * 15),
            price, price, price, price, 0)).ToArray();
}
