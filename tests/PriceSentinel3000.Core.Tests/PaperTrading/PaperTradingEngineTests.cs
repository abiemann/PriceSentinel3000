using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;
using PriceSentinel3000.Core.Tests.Strategy;

namespace PriceSentinel3000.Core.Tests.PaperTrading;

public sealed class PaperTradingEngineTests
{
    [Fact]
    public void Engine_BuysAtAskHonorsQuantityLimitAndStopsAtBid()
    {
        var instrument = new Instrument("SOFI");
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 1_000m,
            QuantityLimitMode = QuantityLimitMode.NoMoreThan,
            MaximumQuantity = 2m,
            StopLossBasis = StopLossBasis.BuyPriceAmount,
            StopLossValue = 1m,
        };
        var engine = new PaperTradingEngine(instrument, settings);
        List<MarketQuote> quotes =
        [
            .. PriceActionSignalEngineTests.Quotes(
            [
                100.00m, 99.95m, 99.90m, 99.85m, 99.80m,
                99.75m, 99.70m, 99.65m, 99.60m, 99.55m,
                99.50m, 99.48m, 99.46m, 99.45m, 99.46m,
                99.45m, 99.46m, 99.45m, 99.47m, 99.50m,
            ]),
        ];

        PaperTradeResult buy = engine.Process(quotes);

        Assert.NotNull(buy.Fill);
        Assert.Equal(PaperOrderSide.Buy, buy.Fill.Side);
        Assert.Equal(2m, buy.Fill.Quantity);
        Assert.Equal(quotes[^1].Ask, buy.Fill.Price);
        Assert.Equal(2m, buy.Account.PositionQuantity);
        Assert.Equal(1, buy.Account.EntriesToday);

        DateTimeOffset beforeStopTime = quotes[^1].SourceTimestampUtc.AddSeconds(5);
        quotes.Add(new(
            instrument,
            beforeStopTime,
            beforeStopTime,
            98.80m,
            98.82m,
            98.81m,
            2_000m));

        PaperTradeResult beforeStop = engine.Process(quotes);

        Assert.NotEqual(StrategySignalKind.StopLoss, beforeStop.Decision.Signal);
        Assert.Equal(2m, beforeStop.Account.PositionQuantity);

        DateTimeOffset stopTime = beforeStopTime.AddSeconds(5);
        quotes.Add(new(
            instrument,
            stopTime,
            stopTime,
            98.40m,
            98.42m,
            98.41m,
            2_000m));

        PaperTradeResult sell = engine.Process(quotes);

        Assert.Equal(StrategySignalKind.StopLoss, sell.Decision.Signal);
        Assert.NotNull(sell.Fill);
        Assert.Equal(PaperOrderSide.Sell, sell.Fill.Side);
        Assert.Equal(98.40m, sell.Fill.Price);
        Assert.Equal(0m, sell.Account.PositionQuantity);
        Assert.True(sell.Account.RealizedProfitLoss < 0m);
        Assert.Contains("per share", sell.Decision.Reasons[0]);
    }

    [Fact]
    public void Engine_DoesNotEvaluateTheSameVenueTimestampTwice()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            PaperTraderSettings.Default);
        IReadOnlyList<MarketQuote> quotes = PriceActionSignalEngineTests.Quotes(
            Enumerable.Repeat(100m, 15).ToArray());

        engine.Process(quotes);
        PaperTradeResult duplicate = engine.Process(quotes);

        Assert.Equal(StrategySignalKind.Hold, duplicate.Decision.Signal);
        Assert.Equal("NO NEW PRICE", duplicate.Decision.State);
    }

    [Fact]
    public void Engine_ReplayBarWithoutBookFillsAtClose()
    {
        var instrument = new Instrument("USO");
        var engine = new PaperTradingEngine(
            instrument,
            PaperTraderSettings.Default,
            new ScriptedStrategy(StrategySignalKind.Buy));
        DateTimeOffset timestamp = new(2026, 7, 31, 20, 52, 0, TimeSpan.Zero);
        var replayBar = new MarketQuote(
            instrument,
            DateTimeOffset.UtcNow,
            timestamp,
            0m,
            0m,
            73.25m,
            1_000m);

        PaperTradeResult result = engine.Process([replayBar]);

        Assert.NotNull(result.Fill);
        Assert.Equal(PaperOrderSide.Buy, result.Fill.Side);
        Assert.Equal(73.25m, result.Fill.Price);
    }

    [Fact]
    public void Engine_LocksFurtherBuysAtMaximumEntryCount()
    {
        var instrument = new Instrument("SOFI");
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 100m,
            UnlimitedEntries = false,
            MaximumEntriesPerDay = 1,
        };
        var strategy = new ScriptedStrategy(
            StrategySignalKind.Buy,
            StrategySignalKind.Sell,
            StrategySignalKind.Buy);
        var engine = new PaperTradingEngine(instrument, settings, strategy);
        DateTimeOffset start = new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
        var quotes = new List<MarketQuote>();

        quotes.Add(Quote(instrument, start, 10m));
        Assert.NotNull(engine.Process(quotes).Fill);
        quotes.Add(Quote(instrument, start.AddSeconds(5), 10.2m));
        Assert.NotNull(engine.Process(quotes).Fill);
        quotes.Add(Quote(instrument, start.AddSeconds(40), 10m));

        PaperTradeResult blocked = engine.Process(quotes);

        Assert.Null(blocked.Fill);
        Assert.Equal(StrategySignalKind.Hold, blocked.Decision.Signal);
        Assert.Equal("RISK BLOCKED", blocked.Decision.State);
        Assert.Contains("Maximum entries", blocked.Decision.Reasons[0]);
    }

    [Fact]
    public void Engine_DailyLossLiquidatesAndLocksAccount()
    {
        var instrument = new Instrument("SOFI");
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 1_000m,
            MaximumDailyLossBasis = AmountBasis.FixedAmount,
            MaximumDailyLossValue = 1m,
            StopLossBasis = StopLossBasis.PositionLossAmount,
            StopLossValue = 1_000m,
        };
        var engine = new PaperTradingEngine(
            instrument,
            settings,
            new ScriptedStrategy(StrategySignalKind.Buy));
        DateTimeOffset start = new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
        var quotes = new List<MarketQuote>
        {
            Quote(instrument, start, 10m),
        };
        Assert.NotNull(engine.Process(quotes).Fill);
        quotes.Add(Quote(instrument, start.AddSeconds(5), 9.95m));

        PaperTradeResult liquidated = engine.Process(quotes);

        Assert.Equal(StrategySignalKind.DailyLoss, liquidated.Decision.Signal);
        Assert.Equal(PaperOrderSide.Sell, liquidated.Fill?.Side);
        Assert.True(liquidated.Account.RiskLocked);
        Assert.Equal(0m, liquidated.Account.PositionQuantity);
    }

    private static MarketQuote Quote(
        Instrument instrument,
        DateTimeOffset timestamp,
        decimal last) =>
        new(
            instrument,
            timestamp,
            timestamp,
            last - 0.01m,
            last + 0.01m,
            last,
            1_000m);

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
