using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Application.Tests.Strategies;

public sealed class CountSinceEngineTests
{
    [Fact]
    public void SessionCounterSurvivesRollingBarsAndResetsOnMissingObservations()
    {
        CompiledThinkScript script = ThinkScriptCompiler.Compile("plot count = CountSince(yes, no); AddOrder(OrderType.BUY_TO_OPEN, count == 600);");
        var engine = new ThinkScriptSignalEngine(script, 15);
        for (int index = 0; index < 600; index++)
        {
            engine.Bars.ObserveHistoricalBar(Quote(index));
            StrategyDecision decision = engine.Evaluate([Quote(index + 1)], StrategyPositionContext.Flat);
            Assert.Equal(index + 1, engine.LastEvaluation!.Proposal.Plots["count"]);
            Assert.Equal(index == 599 ? StrategySignalKind.Buy : StrategySignalKind.Hold, decision.Signal);
            Assert.Equal("WAITING FOR CANDLE", engine.Evaluate([Quote(index + 1)], StrategyPositionContext.Flat).State);
        }
        Assert.Equal(256, engine.Bars.Snapshot().Count);
        engine.Bars.ObserveHistoricalBar(Quote(605));
        engine.Evaluate([Quote(606)], StrategyPositionContext.Flat);
        Assert.Equal(1, engine.LastEvaluation!.Proposal.Plots["count"]);
        Assert.True(engine.Bars.ContinuityVersion > 0);

        var fresh = new ThinkScriptSignalEngine(script, 15);
        fresh.Bars.ObserveHistoricalBar(Quote(606));
        fresh.Evaluate([Quote(607)], StrategyPositionContext.Flat);
        Assert.Equal(1, fresh.LastEvaluation!.Proposal.Plots["count"]);
    }

    private static MarketQuote Quote(int index)
    {
        DateTimeOffset at = DateTimeOffset.UnixEpoch.AddSeconds(index * 15);
        return new(new Instrument("NVDA"), at, at, 20, 20, 20, 0);
    }
}
