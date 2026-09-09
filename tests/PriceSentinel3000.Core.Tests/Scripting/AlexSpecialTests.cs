using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.Scripting;

public sealed class AlexSpecialTests
{
    private static readonly StrategyPositionContext Held = new(1, 1000, DateTimeOffset.UnixEpoch);

    [Fact]
    public void SourceUsesChartRsiAndFifteenSecondReferenceInterval()
    {
        string source = ReadSource();
        CompiledThinkScript script = Compile();
        Assert.Contains("# PriceSentinel: tested-candle-seconds=15", source);
        Assert.Contains("averageType = AverageType.SIMPLE", source);
        Assert.Equal(14, script.DefaultInputs["rsiLength"]);
        Assert.Equal(16, script.RequiredWarmupBars);
        Assert.Equal("WARMING UP", script.Evaluate(Bars(Enumerable.Repeat(1000m, 15)), Held).State);
    }

    [Theory]
    [InlineData(1, 9, ScriptAction.Buy)] // RSI exactly 10.
    [InlineData(1, 10, ScriptAction.Buy)]
    [InlineData(2, 9, ScriptAction.Hold)]
    public void DeepDipUsesInclusiveTenThreshold(decimal gain, decimal loss, ScriptAction expected)
    {
        CompiledThinkScript script = Compile();
        // The last fourteen changes contain one gain and one loss; all others are zero.
        decimal[] prices = [1000, 1000, 1000 + gain, .. Enumerable.Repeat(1000 + gain - loss, 13)];
        ScriptProposal proposal = script.Evaluate(Bars(prices), StrategyPositionContext.Flat);
        Assert.Equal(expected, proposal.Action);
        if (expected == ScriptAction.Buy) Assert.Equal("RSI 10 or below", proposal.Reason);
        Assert.Equal(ScriptAction.Hold, script.Evaluate(Bars(prices), Held).Action);
    }

    [Theory]
    [InlineData(6, 4, ScriptAction.Sell)] // RSI exactly 60.
    [InlineData(7, 3, ScriptAction.Sell)]
    [InlineData(59, 41, ScriptAction.Hold)]
    public void ExitUsesInclusiveSixtyThreshold(decimal gain, decimal loss, ScriptAction expected)
    {
        CompiledThinkScript script = Compile();
        decimal[] prices = [1000, 1000, 1000 + gain, .. Enumerable.Repeat(1000 + gain - loss, 13)];
        Assert.Equal(expected, script.Evaluate(Bars(prices), Held).Action);
        Assert.Equal(ScriptAction.Hold, script.Evaluate(Bars(prices), StrategyPositionContext.Flat).Action);
    }

    [Fact]
    public void ThirdDipCountsSeparateCrossingsAndDoesNotBuyOnFirstSecondOrFourth()
    {
        CompiledThinkScript script = Compile();
        decimal[] targets = [25, 20, 24, 35, 25, 22, 35, 25, 20, 35, 25];
        StrategyBar[] bars = Bars(PricesForRsiTargets(targets));
        int prelude = bars.Length - targets.Length;
        int[] expectedCounts = [1, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4];
        for (int index = 0; index < targets.Length; index++)
        {
            ScriptProposal proposal = script.Evaluate(bars[..(prelude + index + 1)], StrategyPositionContext.Flat);
            Assert.Equal(targets[index], Indicator(proposal, "strength"), 8);
            Assert.Equal(expectedCounts[index], Indicator(proposal, "dipCount"));
            Assert.Equal(index == 7 ? ScriptAction.Buy : ScriptAction.Hold, proposal.Action);
            if (index == 7)
            {
                Assert.Equal("Third dip below RSI 30", proposal.Reason);
                Assert.Equal(ScriptAction.Hold, script.Evaluate(bars[..(prelude + index + 1)], Held).Action);
            }
        }
    }

    [Fact]
    public void ReachingSixtyResetsTheDipSequenceAndSellsAnOpenPosition()
    {
        CompiledThinkScript script = Compile();
        decimal[] targets = [25, 35, 25, 65, 35, 25, 35, 25, 35, 25];
        StrategyBar[] bars = Bars(PricesForRsiTargets(targets));
        int prelude = bars.Length - targets.Length;
        int[] expectedCounts = [1, 1, 2, 0, 0, 1, 1, 2, 2, 3];
        for (int index = 0; index < targets.Length; index++)
        {
            ScriptProposal proposal = script.Evaluate(bars[..(prelude + index + 1)], StrategyPositionContext.Flat);
            Assert.Equal(expectedCounts[index], Indicator(proposal, "dipCount"));
            Assert.Equal(index == 9 ? ScriptAction.Buy : ScriptAction.Hold, proposal.Action);
            if (index == 3) Assert.Equal(ScriptAction.Sell, script.Evaluate(bars[..(prelude + index + 1)], Held).Action);
        }
    }

    private static CompiledThinkScript Compile()
    {
        CompiledThinkScript script = ThinkScriptCompiler.Compile(ReadSource());
        Assert.True(script.IsCompatible, string.Join("; ", script.Diagnostics.Select(item => item.Message)));
        return script;
    }

    private static string ReadSource() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Strategies", "Alex Special.thinkscript"));

    private static decimal Indicator(ScriptProposal proposal, string name) =>
        Assert.Single(proposal.Indicators, indicator => indicator.Name == name).Value!.Value;

    private static StrategyBar[] Bars(IEnumerable<decimal> prices) => prices.Select((price, index) => new StrategyBar(
        DateTimeOffset.UnixEpoch.AddSeconds(index * 15), DateTimeOffset.UnixEpoch.AddSeconds((index + 1) * 15),
        price, price, price, price, 0, false)).ToArray();

    private static decimal[] PricesForRsiTargets(decimal[] targets)
    {
        // Invert the simple RSI ratio for the next change, independently of the interpreter.
        var prices = Enumerable.Range(0, 30).Select(index => 1000m + index % 2).ToList();
        foreach (decimal target in targets)
        {
            decimal[] previous = prices.TakeLast(14).ToArray();
            decimal[] changes = previous.Zip(previous.Skip(1), (left, right) => right - left).ToArray();
            decimal gains = changes.Where(change => change > 0).Sum();
            decimal losses = -changes.Where(change => change < 0).Sum();
            decimal change = gains * 100 / (gains + losses) <= target
                ? losses * target / (100 - target) - gains
                : -(gains * (100 - target) / target - losses);
            prices.Add(prices[^1] + change);
        }
        return prices.ToArray();
    }
}
