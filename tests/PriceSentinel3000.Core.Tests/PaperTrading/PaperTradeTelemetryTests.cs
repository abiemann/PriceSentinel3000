using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.PaperTrading;

public sealed class PaperTradeTelemetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, "STOP LOSS")]
    [InlineData(true, "DAILY LOSS LIMIT")]
    public void RiskLiquidationPreemptsStrategyAndRetainsNoStaleProposal(bool dailyLoss, string expected)
    {
        var strategy = new CountingStrategy(StrategySignalKind.Buy);
        var engine = new PaperTradingEngine(new("SOFI"), TradingSessionSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 100,
            StopLossBasis = StopLossBasis.TotalPositionLossAmount,
            StopLossValue = dailyLoss ? 1_000 : 1,
            MaximumDailyLossBasis = AmountBasis.FixedAmount,
            MaximumDailyLossValue = dailyLoss ? 1 : 1_000,
        }, strategy);
        PaperTradeResult buy = engine.Process([Quote(0, 10)]);
        Assert.True(buy.StrategyEvaluated);
        Assert.Same(buy.Decision, buy.StrategyProposal);
        Assert.Null(buy.RiskOverride);
        Assert.Equal(PaperOrderSide.Buy, buy.Fill!.Side);

        PaperTradeResult stop = engine.Process([Quote(15, 9)]);
        Assert.Equal(1, strategy.EvaluationCount);
        Assert.False(stop.StrategyEvaluated);
        Assert.Null(stop.StrategyProposal);
        Assert.Equal(expected, stop.RiskOverride);
        Assert.Equal(expected, stop.Decision.State);
        Assert.Equal(PaperOrderSide.Sell, stop.Fill!.Side);

        PaperTradeResult duplicate = engine.Process([Quote(15, 9)]);
        PaperTradeResult empty = engine.Process([]);
        Assert.All(new[] { duplicate, empty }, result =>
        {
            Assert.False(result.StrategyEvaluated);
            Assert.Null(result.StrategyProposal);
            Assert.Null(result.RiskOverride);
            Assert.Null(result.Fill);
        });
        Assert.Equal(1, strategy.EvaluationCount);
    }

    [Fact]
    public void EntryLimitKeepsTheOriginalBuyAndTheHostOverride()
    {
        var engine = new PaperTradingEngine(new("SOFI"), TradingSessionSettings.Default with
        {
            UnlimitedEntries = false,
            MaximumEntriesPerDay = 1,
        }, new CountingStrategy(StrategySignalKind.Buy, StrategySignalKind.Sell, StrategySignalKind.Buy));
        engine.Process([Quote(0, 10)]);
        PaperTradeResult sell = engine.Process([Quote(15, 11)]);
        Assert.Same(sell.Decision, sell.StrategyProposal);
        Assert.Null(sell.RiskOverride);

        PaperTradeResult blocked = engine.Process([Quote(60, 12)]);

        Assert.True(blocked.StrategyEvaluated);
        Assert.Equal(StrategySignalKind.Buy, blocked.StrategyProposal!.Signal);
        Assert.Equal("Original proposal", blocked.StrategyProposal.Reasons[0]);
        Assert.Equal(StrategySignalKind.Hold, blocked.Decision.Signal);
        Assert.Equal("RISK BLOCKED", blocked.RiskOverride);
        Assert.Contains("Maximum entries", blocked.Decision.Reasons[0]);
        Assert.Null(blocked.Order);
        Assert.Null(blocked.Fill);
    }

    [Fact]
    public void UnfillableQuantityReportsBuyingPowerOverrideWithoutLosingBuyProposal()
    {
        var engine = new PaperTradingEngine(new("SOFI"), TradingSessionSettings.Default with
        {
            QuantityLimitMode = QuantityLimitMode.NoMoreThan,
            MaximumQuantity = 0.0000009m,
        }, new CountingStrategy(StrategySignalKind.Buy));

        PaperTradeResult blocked = engine.Process([Quote(0, 10)]);

        Assert.Equal(StrategySignalKind.Buy, blocked.StrategyProposal!.Signal);
        Assert.Equal("NO BUYING POWER", blocked.RiskOverride);
        Assert.Equal(StrategySignalKind.Hold, blocked.Decision.Signal);
        Assert.Equal(0, blocked.Account.EntriesToday);
        Assert.Null(blocked.Fill);
    }

    private static MarketQuote Quote(int seconds, decimal price) =>
        new(new("SOFI"), Start.AddSeconds(seconds), Start.AddSeconds(seconds), price, price, price, 0);

    private sealed class CountingStrategy(params StrategySignalKind[] signals) : IPriceActionSignalEngine
    {
        public int EvaluationCount { get; private set; }
        public StrategyDecision Evaluate(IReadOnlyList<MarketQuote> quotes, StrategyPositionContext position)
        {
            StrategySignalKind signal = signals[Math.Min(EvaluationCount++, signals.Length - 1)];
            return new(quotes[^1].SourceTimestampUtc, signal, signal.ToString(), 1, ["Original proposal"], null, 0, 0);
        }
    }
}
