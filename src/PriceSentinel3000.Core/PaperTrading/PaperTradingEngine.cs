using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.PaperTrading;

public enum PaperOrderSide
{
    Buy,
    Sell,
}

public sealed record PaperOrder(
    Guid Id,
    DateTimeOffset SubmittedAtUtc,
    PaperOrderSide Side,
    decimal Quantity,
    decimal ExpectedPrice,
    string Reason);

public sealed record PaperFill(
    Guid OrderId,
    DateTimeOffset FilledAtUtc,
    PaperOrderSide Side,
    decimal Quantity,
    decimal Price,
    decimal RealizedProfitLoss);

public sealed record PaperAccountSnapshot(
    decimal Cash,
    decimal BuyingPower,
    decimal Equity,
    decimal PositionQuantity,
    decimal AveragePrice,
    decimal MarketValue,
    decimal RealizedProfitLoss,
    decimal UnrealizedProfitLoss,
    int EntriesToday,
    bool RiskLocked);

public sealed record PaperTradeResult(
    StrategyDecision Decision,
    PaperOrder? Order,
    PaperFill? Fill,
    PaperAccountSnapshot Account);

/// <summary>
/// Executes strategy decisions against an in-memory paper account. This class
/// has no broker dependency and cannot place a real order.
/// </summary>
public sealed class PaperTradingEngine
{
    private static readonly TimeSpan ReentryCooldown = TimeSpan.FromSeconds(30);
    private readonly PaperTraderSettings _settings;
    private readonly IPriceActionSignalEngine _strategy;
    private decimal _cash;
    private decimal _positionQuantity;
    private decimal _averagePrice;
    private DateTimeOffset? _openedAtUtc;
    private DateTimeOffset? _lastExitUtc;
    private DateTimeOffset? _lastEvaluatedUtc;
    private decimal _realizedProfitLoss;
    private int _entriesToday;
    private bool _riskLocked;

    public PaperTradingEngine(
        Instrument instrument,
        PaperTraderSettings settings,
        IPriceActionSignalEngine? strategy = null)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _strategy = strategy ?? new PriceActionSignalEngine(settings.BufferMinutes);
        _cash = settings.StartingBalance;
    }

    public PaperTradeResult Process(IReadOnlyList<MarketQuote> quotes)
    {
        ArgumentNullException.ThrowIfNull(quotes);

        if (quotes.Count == 0)
        {
            StrategyDecision empty = StrategyDecision.Hold(
                DateTimeOffset.UtcNow,
                "WARMING UP",
                "Waiting for market data.");
            return new(empty, null, null, Snapshot(0m));
        }

        MarketQuote latest = quotes[^1];
        decimal mark = ExitPrice(latest);

        if (_lastEvaluatedUtc is not null && latest.SourceTimestampUtc <= _lastEvaluatedUtc)
        {
            StrategyDecision duplicate = StrategyDecision.Hold(
                latest.SourceTimestampUtc,
                "NO NEW PRICE",
                "This venue timestamp was already evaluated.");
            return new(duplicate, null, null, Snapshot(mark));
        }

        _lastEvaluatedUtc = latest.SourceTimestampUtc;
        StrategyDecision decision = EvaluateRisk(latest, mark) ?? _strategy.Evaluate(
            quotes,
            _positionQuantity > 0m && _openedAtUtc is not null
                ? new(_positionQuantity, _averagePrice, _openedAtUtc.Value)
                : StrategyPositionContext.Flat);

        if (decision.Signal is StrategySignalKind.Buy)
        {
            string? blocked = BuyBlockReason(latest.SourceTimestampUtc, mark);

            if (blocked is not null)
            {
                decision = decision with
                {
                    Signal = StrategySignalKind.Hold,
                    State = "RISK BLOCKED",
                    Confidence = 0m,
                    Reasons = [blocked],
                };
            }
            else
            {
                return FillBuy(latest, decision);
            }
        }
        else if (decision.Signal is StrategySignalKind.Sell or
                 StrategySignalKind.StopLoss or
                 StrategySignalKind.DailyLoss)
        {
            if (_positionQuantity > 0m)
            {
                return FillSell(latest, decision);
            }
        }

        return new(decision, null, null, Snapshot(mark));
    }

    private StrategyDecision? EvaluateRisk(MarketQuote latest, decimal mark)
    {
        if (_positionQuantity <= 0m)
        {
            return null;
        }

        decimal unrealizedLoss = Math.Max(0m, (_averagePrice - mark) * _positionQuantity);
        decimal stopLimit = _settings.StopLossBasis switch
        {
            StopLossBasis.FixedAmount => _settings.StopLossValue,
            _ => _averagePrice * _positionQuantity * _settings.StopLossValue / 100m,
        };

        if (unrealizedLoss >= stopLimit)
        {
            return new(
                latest.SourceTimestampUtc,
                StrategySignalKind.StopLoss,
                "STOP LOSS",
                1m,
                [$"Paper position loss ${unrealizedLoss:0.00} reached the ${stopLimit:0.00} stop."],
                null,
                0m,
                _averagePrice == 0m ? 0m : (mark - _averagePrice) / _averagePrice * 100m);
        }

        decimal equity = _cash + _positionQuantity * mark;
        decimal dailyLoss = Math.Max(0m, _settings.StartingBalance - equity);
        decimal dailyLimit = DailyLossLimit();

        if (dailyLoss >= dailyLimit)
        {
            _riskLocked = true;
            return new(
                latest.SourceTimestampUtc,
                StrategySignalKind.DailyLoss,
                "DAILY LOSS LIMIT",
                1m,
                [$"Paper-account drawdown ${dailyLoss:0.00} reached the ${dailyLimit:0.00} daily limit."],
                null,
                0m,
                _settings.StartingBalance == 0m ? 0m : -dailyLoss / _settings.StartingBalance * 100m);
        }

        return null;
    }

    private string? BuyBlockReason(DateTimeOffset now, decimal mark)
    {
        if (_riskLocked || Math.Max(0m, _settings.StartingBalance - Snapshot(mark).Equity) >= DailyLossLimit())
        {
            _riskLocked = true;
            return "Maximum daily paper loss reached; new entries are locked for this session.";
        }

        if (!_settings.UnlimitedEntries && _entriesToday >= _settings.MaximumEntriesPerDay)
        {
            return "Maximum entries per day reached.";
        }

        if (_lastExitUtc is not null && now - _lastExitUtc < ReentryCooldown)
        {
            return "Waiting for the 30-second re-entry cooldown.";
        }

        return null;
    }

    private PaperTradeResult FillBuy(MarketQuote quote, StrategyDecision decision)
    {
        decimal fillPrice = EntryPrice(quote);
        decimal equity = Snapshot(quote.Last).Equity;
        decimal allocation = _settings.PositionSizeBasis switch
        {
            AmountBasis.FixedAmount => _settings.PositionSizeValue,
            _ => equity * _settings.PositionSizeValue / 100m,
        };
        allocation = Math.Min(allocation, _cash);
        decimal quantity = FloorQuantity(allocation / fillPrice);

        if (_settings.QuantityLimitMode is QuantityLimitMode.MaximumShares)
        {
            quantity = Math.Min(quantity, _settings.MaximumQuantity);
        }

        if (quantity <= 0m)
        {
            StrategyDecision blocked = decision with
            {
                Signal = StrategySignalKind.Hold,
                State = "NO BUYING POWER",
                Confidence = 0m,
                Reasons = ["Paper buying power is too low for a positive fractional quantity."],
            };
            return new(blocked, null, null, Snapshot(quote.Last));
        }

        decimal cost = quantity * fillPrice;
        _cash -= cost;
        _positionQuantity = quantity;
        _averagePrice = fillPrice;
        _openedAtUtc = quote.SourceTimestampUtc;
        _entriesToday++;
        var order = new PaperOrder(
            Guid.NewGuid(),
            quote.SourceTimestampUtc,
            PaperOrderSide.Buy,
            quantity,
            fillPrice,
            decision.State);
        var fill = new PaperFill(
            order.Id,
            quote.SourceTimestampUtc,
            PaperOrderSide.Buy,
            quantity,
            fillPrice,
            0m);
        return new(decision, order, fill, Snapshot(quote.Last));
    }

    private PaperTradeResult FillSell(MarketQuote quote, StrategyDecision decision)
    {
        decimal fillPrice = ExitPrice(quote);
        decimal quantity = _positionQuantity;
        decimal realized = (fillPrice - _averagePrice) * quantity;
        _cash += quantity * fillPrice;
        _realizedProfitLoss += realized;
        _positionQuantity = 0m;
        _averagePrice = 0m;
        _openedAtUtc = null;
        _lastExitUtc = quote.SourceTimestampUtc;

        if (decision.Signal is StrategySignalKind.DailyLoss ||
            Math.Max(0m, _settings.StartingBalance - _cash) >= DailyLossLimit())
        {
            _riskLocked = true;
        }

        var order = new PaperOrder(
            Guid.NewGuid(),
            quote.SourceTimestampUtc,
            PaperOrderSide.Sell,
            quantity,
            fillPrice,
            decision.State);
        var fill = new PaperFill(
            order.Id,
            quote.SourceTimestampUtc,
            PaperOrderSide.Sell,
            quantity,
            fillPrice,
            realized);
        return new(decision, order, fill, Snapshot(fillPrice));
    }

    private PaperAccountSnapshot Snapshot(decimal mark)
    {
        decimal marketValue = _positionQuantity * mark;
        decimal unrealized = _positionQuantity * (mark - _averagePrice);
        return new(
            _cash,
            _cash,
            _cash + marketValue,
            _positionQuantity,
            _averagePrice,
            marketValue,
            _realizedProfitLoss,
            unrealized,
            _entriesToday,
            _riskLocked);
    }

    private decimal DailyLossLimit() => _settings.MaximumDailyLossBasis switch
    {
        AmountBasis.FixedAmount => _settings.MaximumDailyLossValue,
        _ => _settings.StartingBalance * _settings.MaximumDailyLossValue / 100m,
    };

    private static decimal EntryPrice(MarketQuote quote) =>
        quote.HasTwoSidedMarket ? quote.Ask : quote.Last;

    private static decimal ExitPrice(MarketQuote quote) =>
        quote.HasTwoSidedMarket ? quote.Bid : quote.Last;

    private static decimal FloorQuantity(decimal quantity) =>
        Math.Floor(quantity * 1_000_000m) / 1_000_000m;
}
