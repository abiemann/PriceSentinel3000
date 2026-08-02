using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.LiveTrading;

public sealed class LiveExecutionEngineTests
{
    private static readonly Instrument Instrument = new("SOFI");

    [Fact]
    public void Evaluate_BuyUsesBrokerBuyingPowerAndQuantityLimit()
    {
        var engine = new LiveExecutionEngine(
            Settings() with
            {
                PositionSizeValue = 1_000m,
                QuantityLimitMode = QuantityLimitMode.NoMoreThan,
                MaximumQuantity = 2m,
            },
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Buy));

        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(10m)],
            Snapshot(totalValue: 10_000m, buyingPower: 30m));

        Assert.NotNull(result.Intent);
        Assert.Equal(BrokerOrderSide.Buy, result.Intent.Side);
        Assert.Equal(2m, result.Intent.Quantity);
        Assert.NotEqual(Guid.Empty, result.Intent.ClientReferenceId);
    }

    [Fact]
    public void Evaluate_BlocksWhenBrokerHasAnyNonTerminalOrder()
    {
        var engine = new LiveExecutionEngine(
            Settings(),
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Buy));
        BrokerOrderSnapshot unknownOrder = Order(
            BrokerOrderSide.Buy,
            BrokerOrderState.Unknown,
            1m);

        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(10m)],
            Snapshot(openOrders: [unknownOrder]));

        Assert.Null(result.Intent);
        Assert.Equal("BROKER BLOCKED", result.Decision.State);
        Assert.Contains("open Robinhood order", result.Decision.Reasons[0]);
    }

    [Fact]
    public void Evaluate_StopLossSellsOnlySharesAvailableForSale()
    {
        var engine = new LiveExecutionEngine(
            Settings() with
            {
                StopLossBasis = StopLossBasis.PurchasePriceDeclinePercentage,
                StopLossValue = 1m,
            },
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Hold));
        BrokerPosition position = new("SOFI", 4m, 10m, 3m, 1m);

        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(9.85m)],
            Snapshot(position: position));

        Assert.NotNull(result.Intent);
        Assert.Equal(StrategySignalKind.StopLoss, result.Decision.Signal);
        Assert.Equal(BrokerOrderSide.Sell, result.Intent.Side);
        Assert.Equal(3m, result.Intent.Quantity);
    }

    [Fact]
    public void Evaluate_DailyLossLocksNewEntries()
    {
        var engine = new LiveExecutionEngine(
            Settings() with
            {
                MaximumDailyLossBasis = AmountBasis.FixedAmount,
                MaximumDailyLossValue = 50m,
            },
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Buy));

        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(10m)],
            Snapshot(totalValue: 9_949m));

        Assert.True(result.RiskLocked);
        Assert.Null(result.Intent);
        Assert.Equal("DAILY LOSS LOCK", result.Decision.State);
    }

    [Theory]
    [InlineData(10.01)]
    [InlineData(9.97)]
    public void Evaluate_RequiresPriceMovementFromPreviousSellBeforeReentry(
        double movedLast)
    {
        DateTimeOffset sellAt = new(2026, 8, 3, 15, 58, 0, TimeSpan.Zero);
        var engine = new LiveExecutionEngine(
            Settings(),
            10_000m,
            new ScriptedStrategy(
                StrategySignalKind.Buy,
                StrategySignalKind.Buy));
        BrokerOrderSnapshot sell = Order(
            BrokerOrderSide.Sell,
            BrokerOrderState.Filled,
            1m) with
        {
            AveragePrice = 10m,
            UpdatedAtUtc = sellAt,
        };
        engine.ObserveTerminalOrder(sell);

        LiveTradeEvaluation unchanged = engine.Evaluate(
            [Quote(9.99m)],
            Snapshot());

        Assert.Null(unchanged.Intent);
        Assert.Equal("RISK BLOCKED", unchanged.Decision.State);
        Assert.Contains("0.10%", unchanged.Decision.Reasons[0]);
        Assert.Contains("last sell", unchanged.Decision.Reasons[0]);

        LiveTradeEvaluation moved = engine.Evaluate(
            [Quote((decimal)movedLast, secondsOffset: 5)],
            Snapshot());

        Assert.NotNull(moved.Intent);
        Assert.Equal(BrokerOrderSide.Buy, moved.Intent.Side);
    }

    [Fact]
    public void ObserveTerminalOrder_IsIdempotentForEntryLimits()
    {
        var engine = new LiveExecutionEngine(
            Settings() with
            {
                UnlimitedEntries = false,
                MaximumEntriesPerDay = 1,
            },
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Buy));
        BrokerOrderSnapshot fill = Order(
            BrokerOrderSide.Buy,
            BrokerOrderState.Filled,
            1m);

        engine.ObserveTerminalOrder(fill);
        engine.ObserveTerminalOrder(fill);
        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(10m)],
            Snapshot());

        Assert.Equal(1, engine.EntriesToday);
        Assert.Null(result.Intent);
        Assert.Equal("RISK BLOCKED", result.Decision.State);
        Assert.Contains("Maximum entries", result.Decision.Reasons[0]);
    }

    [Fact]
    public void ObserveTerminalOrder_CountsPartiallyFilledCancelledBuyAsEntry()
    {
        var engine = new LiveExecutionEngine(
            Settings(),
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Hold));
        BrokerOrderSnapshot partialFill = Order(
            BrokerOrderSide.Buy,
            BrokerOrderState.PartiallyFilledRestCancelled,
            2m) with
        {
            FilledQuantity = 1m,
            AveragePrice = 10m,
            Executions =
            [
                new("partial-execution", DateTimeOffset.UtcNow, 1m, 10m),
            ],
        };

        engine.ObserveTerminalOrder(partialFill);
        engine.ObserveTerminalOrder(partialFill);

        Assert.Equal(1, engine.EntriesToday);
    }

    [Fact]
    public void Constructor_RestoresEntriesFromEarlierLiveSessions()
    {
        var engine = new LiveExecutionEngine(
            Settings() with
            {
                UnlimitedEntries = false,
                MaximumEntriesPerDay = 1,
            },
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Buy),
            initialEntriesToday: 1);

        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(10m)],
            Snapshot());

        Assert.Equal(1, engine.EntriesToday);
        Assert.Null(result.Intent);
        Assert.Equal("RISK BLOCKED", result.Decision.State);
        Assert.Contains("Maximum entries", result.Decision.Reasons[0]);
    }

    [Fact]
    public void Evaluate_BlocksPositionWithoutAveragePrice()
    {
        var engine = new LiveExecutionEngine(
            Settings(),
            10_000m,
            new ScriptedStrategy(StrategySignalKind.Sell));

        LiveTradeEvaluation result = engine.Evaluate(
            [Quote(10m)],
            Snapshot(position: new("SOFI", 2m, 0m, 2m, 0m)));

        Assert.Null(result.Intent);
        Assert.Equal("BROKER BLOCKED", result.Decision.State);
        Assert.Contains("average purchase price", result.Decision.Reasons[0]);
    }

    private static PaperTraderSettings Settings() => PaperTraderSettings.Default with
    {
        Symbol = "SOFI",
        PositionSizeBasis = AmountBasis.FixedAmount,
        PositionSizeValue = 100m,
        MaximumDailyLossBasis = AmountBasis.FixedAmount,
        MaximumDailyLossValue = 100m,
        StopLossBasis = StopLossBasis.TotalPositionLossAmount,
        StopLossValue = 100m,
    };

    private static MarketQuote Quote(decimal last, int secondsOffset = 0)
    {
        DateTimeOffset now = new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero)
            .AddSeconds(secondsOffset);
        return new(Instrument, now, now, last - 0.01m, last + 0.01m, last, 1_000m);
    }

    private static LiveBrokerSnapshot Snapshot(
        decimal totalValue = 10_000m,
        decimal buyingPower = 10_000m,
        BrokerPosition? position = null,
        IReadOnlyList<BrokerOrderSnapshot>? openOrders = null) =>
        new(
            new("12344242", true, true, "cash"),
            new(totalValue, totalValue, buyingPower, buyingPower, "USD"),
            position ?? BrokerPosition.Flat("SOFI"),
            new("SOFI", true, true, "active", null),
            openOrders ?? [],
            DateTimeOffset.UtcNow);

    private static BrokerOrderSnapshot Order(
        BrokerOrderSide side,
        BrokerOrderState state,
        decimal quantity)
    {
        Guid reference = Guid.NewGuid();
        return new(
            reference,
            Guid.NewGuid().ToString("D"),
            "SOFI",
            side,
            state,
            quantity,
            state is BrokerOrderState.Filled ? quantity : 0m,
            state is BrokerOrderState.Filled ? 10m : null,
            null,
            DateTimeOffset.UtcNow,
            state is BrokerOrderState.Filled
                ? [new(reference.ToString("D"), DateTimeOffset.UtcNow, quantity, 10m)]
                : []);
    }

    private sealed class ScriptedStrategy(params StrategySignalKind[] signals)
        : IPriceActionSignalEngine
    {
        private readonly Queue<StrategySignalKind> _signals = new(signals);

        public StrategyDecision Evaluate(
            IReadOnlyList<MarketQuote> quotes,
            StrategyPositionContext position)
        {
            StrategySignalKind signal = _signals.Count > 0
                ? _signals.Dequeue()
                : StrategySignalKind.Hold;
            return new(
                quotes[^1].SourceTimestampUtc,
                signal,
                signal.ToString().ToUpperInvariant(),
                1m,
                ["Scripted test signal."],
                50m,
                0m,
                0m);
        }
    }
}
