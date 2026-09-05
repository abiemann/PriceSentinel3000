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
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 1_000m,
            QuantityLimitMode = QuantityLimitMode.NoMoreThan,
            MaximumQuantity = 2m,
            StopLossBasis = StopLossBasis.PurchasePriceDeclinePercentage,
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
        Assert.Contains("purchase-price", sell.Decision.Reasons[0]);
    }

    [Fact]
    public void Engine_DoesNotEvaluateTheSameVenueTimestampTwice()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default);
        IReadOnlyList<MarketQuote> quotes = PriceActionSignalEngineTests.Quotes(
            Enumerable.Repeat(100m, 15).ToArray());

        engine.Process(quotes);
        PaperTradeResult duplicate = engine.Process(quotes);

        Assert.Equal(StrategySignalKind.Hold, duplicate.Decision.Signal);
        Assert.Equal("NO NEW PRICE", duplicate.Decision.State);
    }

    [Fact]
    public void Engine_FloorsTheFinalQuantityCapToBrokerPrecision()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default with
            {
                QuantityLimitMode = QuantityLimitMode.NoMoreThan,
                MaximumQuantity = 1.23456789m,
            },
            new ScriptedStrategy(StrategySignalKind.Buy));

        PaperTradeResult result = engine.Process([Bar(DateTimeOffset.UtcNow, 10m)]);

        Assert.Equal(1.234567m, result.Fill?.Quantity);
    }

    [Fact]
    public void Engine_BlocksACapSmallerThanBrokerPrecision()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default with
            {
                QuantityLimitMode = QuantityLimitMode.NoMoreThan,
                MaximumQuantity = 0.0000009m,
            },
            new ScriptedStrategy(StrategySignalKind.Buy));

        PaperTradeResult result = engine.Process([Bar(DateTimeOffset.UtcNow, 10m)]);

        Assert.Null(result.Fill);
        Assert.Equal(0, result.Account.EntriesToday);
        Assert.Equal(TradingSessionSettings.Default.StartingBalance, result.Account.Cash);
    }

    [Fact]
    public void Engine_ResetsEntryLimitAtEasternMidnightInsteadOfUtcMidnight()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default with { MaximumEntriesPerDay = 1 },
            new ScriptedStrategy(
                StrategySignalKind.Buy,
                StrategySignalKind.Sell,
                StrategySignalKind.Buy,
                StrategySignalKind.Buy));
        DateTimeOffset start = new(2026, 8, 3, 23, 59, 0, TimeSpan.Zero);
        engine.Process([Bar(start, 10m)]);
        engine.Process([Bar(start.AddSeconds(5), 10.2m)]);

        PaperTradeResult sameDay = engine.Process([Bar(start.AddMinutes(1), 10.3m)]);
        PaperTradeResult nextDay = engine.Process([Bar(start.AddHours(4).AddMinutes(1), 10.3m)]);

        Assert.Null(sameDay.Fill);
        Assert.Contains("Maximum entries", sameDay.Decision.Reasons[0]);
        Assert.Equal(PaperOrderSide.Buy, nextDay.Fill?.Side);
        Assert.Equal(1, nextDay.Account.EntriesToday);
    }

    [Theory]
    [InlineData(AmountBasis.FixedAmount, 100)]
    [InlineData(AmountBasis.AccountPercentage, 1)]
    public void Engine_NewDayLossUsesPriorDayMarkedEquityAndKeepsPosition(
        AmountBasis lossBasis,
        int lossValue)
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default with
            {
                StartingBalance = 10_000m,
                PositionSizeBasis = AmountBasis.FixedAmount,
                PositionSizeValue = 10_000m,
                StopLossBasis = StopLossBasis.TotalPositionLossAmount,
                StopLossValue = 10_000m,
                MaximumDailyLossBasis = lossBasis,
                MaximumDailyLossValue = lossValue,
            },
            new ScriptedStrategy(StrategySignalKind.Buy, StrategySignalKind.Hold));
        DateTimeOffset priorDay = new(2026, 8, 3, 19, 0, 0, TimeSpan.Zero);
        engine.Process([Bar(priorDay, 100m)]);
        PaperTradeResult gain = engine.Process([Bar(priorDay.AddHours(1), 105m)]);

        PaperTradeResult nextDay = engine.Process([Bar(priorDay.AddDays(1), 102m)]);

        Assert.Equal(10_500m, gain.Account.Equity);
        Assert.Equal(StrategySignalKind.DailyLoss, nextDay.Decision.Signal);
        Assert.Equal(100m, nextDay.Fill?.Quantity);
        Assert.Equal(10_200m, nextDay.Account.Equity);
        Assert.True(nextDay.Account.RiskLocked);
        Assert.Equal(0, nextDay.Account.EntriesToday);
    }

    [Fact]
    public void Engine_NewDayClearsLossLockUsingRemainingEquity()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default with
            {
                StartingBalance = 1_000m,
                PositionSizeBasis = AmountBasis.FixedAmount,
                PositionSizeValue = 1_000m,
                StopLossBasis = StopLossBasis.TotalPositionLossAmount,
                StopLossValue = 1_000m,
                MaximumDailyLossBasis = AmountBasis.FixedAmount,
                MaximumDailyLossValue = 5m,
            },
            new ScriptedStrategy(StrategySignalKind.Buy, StrategySignalKind.Buy));
        DateTimeOffset priorDay = new(2026, 8, 3, 19, 0, 0, TimeSpan.Zero);
        engine.Process([Bar(priorDay, 10m)]);
        PaperTradeResult locked = engine.Process([Bar(priorDay.AddMinutes(1), 9.9m)]);

        PaperTradeResult nextDay = engine.Process([Bar(priorDay.AddDays(1), 10.1m)]);

        Assert.True(locked.Account.RiskLocked);
        Assert.Equal(990m, locked.Account.Equity);
        Assert.False(nextDay.Account.RiskLocked);
        Assert.Equal(PaperOrderSide.Buy, nextDay.Fill?.Side);
        Assert.Equal(1, nextDay.Account.EntriesToday);
    }

    [Fact]
    public void Engine_NewDayPreservesExitCooldownAndReentryPriceGate()
    {
        var engine = new PaperTradingEngine(
            new("SOFI"),
            TradingSessionSettings.Default with { MaximumEntriesPerDay = 1 },
            new ScriptedStrategy(
                StrategySignalKind.Buy,
                StrategySignalKind.Sell,
                StrategySignalKind.Buy,
                StrategySignalKind.Buy));
        DateTimeOffset exitAt = new(2026, 8, 4, 3, 59, 55, TimeSpan.Zero);
        engine.Process([Bar(exitAt.AddSeconds(-15), 10m)]);
        engine.Process([Bar(exitAt, 10.2m)]);

        PaperTradeResult cooldown = engine.Process([Bar(exitAt.AddSeconds(10), 10.3m)]);
        PaperTradeResult unchanged = engine.Process([Bar(exitAt.AddSeconds(35), 10.2m)]);

        Assert.Equal(0, cooldown.Account.EntriesToday);
        Assert.Null(cooldown.Fill);
        Assert.Contains("cooldown", cooldown.Decision.Reasons[0]);
        Assert.Null(unchanged.Fill);
        Assert.Contains("last sell", unchanged.Decision.Reasons[0]);
    }

    [Fact]
    public void Engine_ReplayBarWithoutBookFillsAtClose()
    {
        var instrument = new Instrument("USO");
        var engine = new PaperTradingEngine(
            instrument,
            TradingSessionSettings.Default,
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
        TradingSessionSettings settings = TradingSessionSettings.Default with
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

    [Theory]
    [InlineData(10.01)]
    [InlineData(9.97)]
    public void Engine_RequiresPriceMovementFromPreviousSellBeforeReentry(
        double movedLast)
    {
        var instrument = new Instrument("SOFI");
        var strategy = new ScriptedStrategy(
            StrategySignalKind.Buy,
            StrategySignalKind.Sell,
            StrategySignalKind.Buy,
            StrategySignalKind.Buy);
        var engine = new PaperTradingEngine(
            instrument,
            TradingSessionSettings.Default,
            strategy);
        DateTimeOffset start = new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
        var quotes = new List<MarketQuote>
        {
            Quote(instrument, start, 10m),
        };
        Assert.Equal(PaperOrderSide.Buy, engine.Process(quotes).Fill?.Side);
        quotes.Add(Quote(instrument, start.AddSeconds(5), 10.01m));
        Assert.Equal(PaperOrderSide.Sell, engine.Process(quotes).Fill?.Side);
        quotes.Add(Quote(instrument, start.AddSeconds(40), 9.99m));

        PaperTradeResult unchanged = engine.Process(quotes);

        Assert.Null(unchanged.Fill);
        Assert.Equal("RISK BLOCKED", unchanged.Decision.State);
        Assert.Contains("0.10%", unchanged.Decision.Reasons[0]);
        Assert.Contains("last sell", unchanged.Decision.Reasons[0]);

        quotes.Add(Quote(
            instrument,
            start.AddSeconds(45),
            (decimal)movedLast));
        PaperTradeResult moved = engine.Process(quotes);

        Assert.Equal(PaperOrderSide.Buy, moved.Fill?.Side);
    }

    [Fact]
    public void Engine_DelayedSettlementExcludesSaleProceedsFromBuyingPowerUntilTPlusOne()
    {
        var instrument = new Instrument("SOFI");
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            StartingBalance = 100m,
            TradesSettleImmediately = false,
            PositionSizeBasis = AmountBasis.AccountPercentage,
            PositionSizeValue = 100m,
            QuantityLimitMode = QuantityLimitMode.AsManyAsPossible,
            UnlimitedEntries = true,
        };
        var strategy = new ScriptedStrategy(
            StrategySignalKind.Buy,
            StrategySignalKind.Sell,
            StrategySignalKind.Buy,
            StrategySignalKind.Buy);
        var engine = new PaperTradingEngine(instrument, settings, strategy);
        DateTimeOffset friday = new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset mondaySettlement = new(2026, 8, 10, 13, 30, 0, TimeSpan.Zero);
        var quotes = new List<MarketQuote>
        {
            Quote(instrument, friday, 10m),
        };

        PaperTradeResult buy = engine.Process(quotes);
        Assert.Equal(PaperOrderSide.Buy, buy.Fill?.Side);

        quotes.Add(Quote(instrument, friday.AddSeconds(5), 10.20m));
        PaperTradeResult sell = engine.Process(quotes);

        Assert.Equal(PaperOrderSide.Sell, sell.Fill?.Side);
        Assert.Equal(mondaySettlement, sell.Fill?.ProceedsAvailableAtUtc);
        Assert.True(sell.Account.Cash > settings.StartingBalance);
        Assert.True(sell.Account.BuyingPower < 0.01m);
        Assert.Equal(sell.Account.Cash, sell.Account.Equity);

        quotes.Add(Quote(instrument, mondaySettlement.AddMinutes(-1), 10.20m));
        PaperTradeResult beforeSettlement = engine.Process(quotes);

        Assert.Null(beforeSettlement.Fill);
        Assert.Equal(0, beforeSettlement.Account.EntriesToday);
        Assert.Equal("NO BUYING POWER", beforeSettlement.Decision.State);
        Assert.Contains(
            "unsettled",
            beforeSettlement.Decision.Reasons[0].ToLowerInvariant());

        quotes.Add(Quote(instrument, mondaySettlement, 10.22m));
        PaperTradeResult afterSettlement = engine.Process(quotes);

        Assert.Equal(PaperOrderSide.Buy, afterSettlement.Fill?.Side);
        Assert.Equal(1, afterSettlement.Account.EntriesToday);
        Assert.True(afterSettlement.Account.PositionQuantity > 0m);
    }

    [Fact]
    public void Engine_ImmediateSettlementRestoresBuyingPowerOnSell()
    {
        var instrument = new Instrument("SOFI");
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            StartingBalance = 100m,
            TradesSettleImmediately = true,
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 50m,
        };
        var engine = new PaperTradingEngine(
            instrument,
            settings,
            new ScriptedStrategy(
                StrategySignalKind.Buy,
                StrategySignalKind.Sell));
        DateTimeOffset start = new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
        var quotes = new List<MarketQuote>
        {
            Quote(instrument, start, 10m),
        };

        Assert.Equal(PaperOrderSide.Buy, engine.Process(quotes).Fill?.Side);
        quotes.Add(Quote(instrument, start.AddSeconds(5), 10.20m));

        PaperTradeResult sell = engine.Process(quotes);

        Assert.Equal(PaperOrderSide.Sell, sell.Fill?.Side);
        Assert.Null(sell.Fill?.ProceedsAvailableAtUtc);
        Assert.Equal(sell.Account.Cash, sell.Account.BuyingPower);
    }

    [Fact]
    public void Engine_TotalPositionLossUsesCombinedDollarLoss()
    {
        var instrument = new Instrument("SOFI");
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 100m,
            QuantityLimitMode = QuantityLimitMode.NoMoreThan,
            MaximumQuantity = 2m,
            StopLossBasis = StopLossBasis.TotalPositionLossAmount,
            StopLossValue = 1m,
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

        PaperTradeResult buy = engine.Process(quotes);

        Assert.Equal(2m, buy.Fill?.Quantity);
        Assert.Equal(10.01m, buy.Fill?.Price);

        quotes.Add(Quote(instrument, start.AddSeconds(5), 9.61m));
        PaperTradeResult beforeStop = engine.Process(quotes);

        Assert.NotEqual(StrategySignalKind.StopLoss, beforeStop.Decision.Signal);
        Assert.Equal(2m, beforeStop.Account.PositionQuantity);

        quotes.Add(Quote(instrument, start.AddSeconds(10), 9.41m));
        PaperTradeResult stopped = engine.Process(quotes);

        Assert.Equal(StrategySignalKind.StopLoss, stopped.Decision.Signal);
        Assert.Equal(PaperOrderSide.Sell, stopped.Fill?.Side);
        Assert.Equal(0m, stopped.Account.PositionQuantity);
        Assert.Contains("position loss", stopped.Decision.Reasons[0]);
    }

    [Fact]
    public void Engine_DailyLossLiquidatesAndLocksAccount()
    {
        var instrument = new Instrument("SOFI");
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            PositionSizeBasis = AmountBasis.FixedAmount,
            PositionSizeValue = 1_000m,
            MaximumDailyLossBasis = AmountBasis.FixedAmount,
            MaximumDailyLossValue = 1m,
            StopLossBasis = StopLossBasis.TotalPositionLossAmount,
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

    private static MarketQuote Bar(DateTimeOffset timestamp, decimal price) =>
        new(new("SOFI"), timestamp, timestamp, 0m, 0m, price, 1_000m);

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
