using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Scripting;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Application.Tests.Strategies;

public sealed class ThinkScriptSignalEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluationSnapshotRetainsExactCompletedCandleAndWarmupUntilNextActualEvaluation()
    {
        var engine = new ThinkScriptSignalEngine(ThinkScriptCompiler.Compile(
            "def averagePrice = Average(close, 2); AddOrder(OrderType.BUY_TO_OPEN, close > averagePrice);"), 15);
        engine.Bars.ObserveHistoricalBar(Quote(0, 10));
        engine.Evaluate([Quote(0, 10)], StrategyPositionContext.Flat);
        Assert.Null(engine.LastEvaluation);

        engine.Evaluate([Quote(15, 10)], StrategyPositionContext.Flat);
        ScriptEvaluationSnapshot warmup = Assert.IsType<ScriptEvaluationSnapshot>(engine.LastEvaluation);
        Assert.Equal(Start.AddSeconds(15), warmup.EvaluatedAtUtc);
        Assert.Equal(1, warmup.RetainedBars);
        Assert.Equal(1, warmup.CompletedBarCount);
        Assert.Equal(2, warmup.RequiredWarmupBars);
        Assert.Equal(engine.RequiredWarmupBars, warmup.RequiredWarmupBars);
        Assert.Equal(engine.Bars.Version, warmup.BarVersion);
        Assert.True(warmup.IsWarmingUp);
        Assert.Equal("WARMING UP", warmup.State);
        Assert.Equal(new StrategyBar(Start, Start.AddSeconds(15), 10, 10, 10, 10, 0), warmup.LatestBar);

        engine.Evaluate([Quote(16, 10)], StrategyPositionContext.Flat);
        Assert.Same(warmup, engine.LastEvaluation);
        engine.Bars.ObserveHistoricalBar(Quote(15, 12));
        engine.Evaluate([Quote(29, 12)], StrategyPositionContext.Flat);
        Assert.Same(warmup, engine.LastEvaluation);

        engine.Evaluate([Quote(30, 12)], StrategyPositionContext.Flat);
        ScriptEvaluationSnapshot evaluated = Assert.IsType<ScriptEvaluationSnapshot>(engine.LastEvaluation);
        Assert.NotSame(warmup, evaluated);
        Assert.Equal(Start.AddSeconds(30), evaluated.EvaluatedAtUtc);
        Assert.Equal(2, evaluated.RetainedBars);
        Assert.Equal(2, evaluated.CompletedBarCount);
        Assert.Equal(engine.Bars.Version, evaluated.BarVersion);
        Assert.False(evaluated.IsWarmingUp);
        Assert.Equal(ScriptAction.Buy, evaluated.Proposal.Action);
        Assert.Equal(11, Assert.Single(evaluated.Proposal.Indicators).Value);
        Assert.Equal(12, evaluated.LatestBar!.Close);
        Assert.Equal(1, warmup.RetainedBars);
        Assert.Null(Assert.Single(warmup.Proposal.Indicators).Value);
    }

    [Fact]
    public void StrategyCannotEvaluateBeforeItsCandleEndsOrConsumeThatVersionEarly()
    {
        var engine = new ThinkScriptSignalEngine(
            ThinkScriptCompiler.Compile("AddOrder(OrderType.BUY_TO_OPEN, yes);"), 15);
        engine.Bars.ObserveHistoricalBar(Quote(0, 10));

        StrategyDecision early = engine.Evaluate([Quote(0, 10)], StrategyPositionContext.Flat);
        StrategyDecision available = engine.Evaluate([Quote(15, 10)], StrategyPositionContext.Flat);
        StrategyDecision repeated = engine.Evaluate([Quote(16, 10)], StrategyPositionContext.Flat);

        Assert.Equal(StrategySignalKind.Hold, early.Signal);
        Assert.Equal(StrategySignalKind.Buy, available.Signal);
        Assert.Equal("WAITING FOR CANDLE", repeated.State);
        engine.Bars.ObserveHistoricalBar(Quote(15, 11));
        Assert.Equal(StrategySignalKind.Buy,
            engine.Evaluate([Quote(30, 11)], StrategyPositionContext.Flat).Signal);
    }

    [Fact]
    public void RuntimeBudgetFailureLatchesWhileHostStopLossRemainsActive()
    {
        var engine = new ThinkScriptSignalEngine(ThinkScriptCompiler.Compile(
            "AddOrder(OrderType.BUY_TO_OPEN, Highest(close, 2048) > 0);"), 15);
        for (int index = 0; index < 4096; index++)
            engine.Bars.ObserveHistoricalBar(Quote(index * 15, 10));

        StrategyDecision failed = engine.Evaluate([Quote(4096 * 15, 10)], StrategyPositionContext.Flat);

        Assert.Equal("SCRIPT ERROR", failed.State);
        Assert.NotNull(engine.Fault);
        engine.Bars.ObserveHistoricalBar(Quote(4100 * 15, 10));
        Assert.Equal("SCRIPT ERROR",
            engine.Evaluate([Quote(4101 * 15, 10)], StrategyPositionContext.Flat).State);

        var host = new LiveExecutionEngine(TradingSessionSettings.Default, 10_000m, engine);
        MarketQuote trigger = Quote(4101 * 15 + 1, 8);
        var broker = new LiveBrokerSnapshot(
            new("test-account", true, true, "individual"),
            new(10_000, 0, 10_000, 10_000, "USD"),
            new("SOFI", 1, 10, 1, 0),
            new("SOFI", true, true, "active", null), [], trigger.SourceTimestampUtc);

        LiveTradeEvaluation risk = host.Evaluate([trigger], broker);

        Assert.Equal(StrategySignalKind.StopLoss, risk.Decision.Signal);
        Assert.Equal(BrokerOrderSide.Sell, risk.Intent!.Side);
        Assert.Equal(1m, risk.Intent.Quantity);
    }

    private static MarketQuote Quote(int second, decimal price) =>
        new(new Instrument("SOFI"), Start.AddSeconds(second), Start.AddSeconds(second), price, price, price, 0);
}
