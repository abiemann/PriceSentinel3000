using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.LiveTrading;

public sealed record LiveTradeEvaluation(
    StrategyDecision Decision,
    BrokerOrderIntent? Intent,
    bool RiskLocked);

/// <summary>
/// Converts deterministic strategy decisions into broker-neutral order intents.
/// It never calls a broker and cannot place an order by itself.
/// </summary>
public sealed class LiveExecutionEngine
{
    private static readonly TimeSpan ReentryCooldown = TimeSpan.FromSeconds(30);
    private readonly TradingSessionSettings _settings;
    private readonly IPriceActionSignalEngine _strategy;
    private readonly HashSet<Guid> _observedTerminalOrders = [];
    private readonly decimal _sessionStartingEquity;
    private DateTimeOffset? _lastEvaluatedUtc;
    private DateTimeOffset? _lastExitUtc;
    private decimal? _lastExitPrice;
    private DateTimeOffset? _positionOpenedAtUtc;
    private int _entriesToday;
    private bool _riskLocked;
    private bool _requiresInheritedPositionExit;

    public LiveExecutionEngine(
        TradingSessionSettings settings,
        decimal sessionStartingEquity,
        IPriceActionSignalEngine? strategy = null,
        int initialEntriesToday = 0,
        DateTimeOffset? initialLastExitUtc = null,
        decimal? initialLastExitPrice = null,
        bool requireInheritedPositionExit = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (sessionStartingEquity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionStartingEquity));
        }

        if (initialEntriesToday < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialEntriesToday));
        }

        if ((initialLastExitUtc is null) != (initialLastExitPrice is null) ||
            initialLastExitPrice is <= 0m)
        {
            throw new ArgumentException(
                "The restored last exit requires both a timestamp and a positive fill price.");
        }

        _settings = settings;
        _sessionStartingEquity = sessionStartingEquity;
        _entriesToday = initialEntriesToday;
        _lastExitUtc = initialLastExitUtc;
        _lastExitPrice = initialLastExitPrice;
        _requiresInheritedPositionExit = requireInheritedPositionExit;
        _strategy = strategy ?? new PriceActionSignalEngine(settings.BufferMinutes);
    }

    public int EntriesToday => _entriesToday;
    public bool RiskLocked => _riskLocked;

    public LiveTradeEvaluation Evaluate(
        IReadOnlyList<MarketQuote> quotes,
        LiveBrokerSnapshot broker)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(broker);

        if (quotes.Count == 0)
        {
            return Hold(DateTimeOffset.UtcNow, "WARMING UP", "Waiting for market data.");
        }

        MarketQuote latest = quotes[^1];

        if (_lastEvaluatedUtc is not null &&
            latest.SourceTimestampUtc <= _lastEvaluatedUtc)
        {
            return Hold(latest.SourceTimestampUtc, "NO NEW PRICE", "This venue timestamp was already evaluated.");
        }

        _lastEvaluatedUtc = latest.SourceTimestampUtc;

        string? brokerBlock = BrokerBlockReason(broker);
        if (brokerBlock is not null)
        {
            return Hold(latest.SourceTimestampUtc, "BROKER BLOCKED", brokerBlock);
        }

        if (_requiresInheritedPositionExit && !broker.Position.HasPosition)
        {
            return Hold(
                latest.SourceTimestampUtc,
                "INHERITED POSITION CHECK",
                "The inherited Robinhood position is no longer present, but PriceSentinel has not confirmed its own completed exit; new entries remain blocked.");
        }

        decimal exitMark = latest.HasTwoSidedMarket ? latest.Bid : latest.Last;
        _positionOpenedAtUtc = broker.Position.HasPosition
            ? _positionOpenedAtUtc ?? latest.SourceTimestampUtc
            : null;
        StrategyDecision decision = EvaluateRisk(latest, exitMark, broker) ??
            _strategy.Evaluate(
                quotes,
                broker.Position.HasPosition
                    ? new(
                        broker.Position.Quantity,
                        broker.Position.AverageBuyPrice,
                        _positionOpenedAtUtc!.Value)
                    : StrategyPositionContext.Flat);

        if (decision.Signal is StrategySignalKind.Buy)
        {
            decimal entryPrice = latest.HasTwoSidedMarket ? latest.Ask : latest.Last;
            string? buyBlock = BuyBlockReason(latest.SourceTimestampUtc, entryPrice, broker);
            if (buyBlock is not null)
            {
                return Block(decision, buyBlock);
            }

            decimal allocation = _settings.PositionSizeBasis switch
            {
                AmountBasis.FixedAmount => _settings.PositionSizeValue,
                _ => broker.Portfolio.TotalValue * _settings.PositionSizeValue / 100m,
            };
            allocation = Math.Min(allocation, broker.Portfolio.BuyingPower);
            decimal quantity = FloorQuantity(allocation / entryPrice);

            if (_settings.QuantityLimitMode is QuantityLimitMode.NoMoreThan)
            {
                quantity = Math.Min(quantity, _settings.MaximumQuantity);
            }

            if (quantity <= 0m)
            {
                return Block(decision, "Robinhood buying power is too low for a positive order quantity.");
            }

            return new(
                decision,
                new(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    latest.Instrument.Symbol,
                    BrokerOrderSide.Buy,
                    quantity,
                    decision.State),
                _riskLocked);
        }

        if (decision.Signal is StrategySignalKind.Sell or
            StrategySignalKind.StopLoss or
            StrategySignalKind.DailyLoss)
        {
            decimal quantity = Math.Min(
                broker.Position.Quantity,
                broker.Position.SharesAvailableForSells);

            if (quantity <= 0m)
            {
                return Block(decision, "Robinhood reports no shares currently available to sell.");
            }

            return new(
                decision,
                new(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    latest.Instrument.Symbol,
                    BrokerOrderSide.Sell,
                    FloorQuantity(quantity),
                    decision.State),
                _riskLocked);
        }

        return new(decision, null, _riskLocked);
    }

    public void ConfirmInheritedPositionClosed() =>
        _requiresInheritedPositionExit = false;

    public void ObserveTerminalOrder(BrokerOrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!order.IsTerminal ||
            order.FilledQuantity <= 0m ||
            !_observedTerminalOrders.Add(order.ClientReferenceId))
        {
            return;
        }

        if (order.Side is BrokerOrderSide.Buy)
        {
            _entriesToday++;
            _positionOpenedAtUtc = order.UpdatedAtUtc;
        }
        else
        {
            _lastExitUtc = order.UpdatedAtUtc;
            _lastExitPrice = order.EffectiveAveragePrice;
            _positionOpenedAtUtc = null;
        }
    }

    private StrategyDecision? EvaluateRisk(
        MarketQuote latest,
        decimal mark,
        LiveBrokerSnapshot broker)
    {
        decimal dailyLoss = Math.Max(0m, _sessionStartingEquity - broker.Portfolio.TotalValue);
        decimal dailyLimit = DailyLossLimit();

        if (dailyLoss >= dailyLimit)
        {
            _riskLocked = true;

            if (!broker.Position.HasPosition)
            {
                return StrategyDecision.Hold(
                    latest.SourceTimestampUtc,
                    "DAILY LOSS LOCK",
                    $"Account drawdown ${dailyLoss:0.00} reached the ${dailyLimit:0.00} daily limit.");
            }

            return new(
                latest.SourceTimestampUtc,
                StrategySignalKind.DailyLoss,
                "DAILY LOSS LIMIT",
                1m,
                [$"Account drawdown ${dailyLoss:0.00} reached the ${dailyLimit:0.00} daily limit; liquidating the monitored position."],
                null,
                0m,
                -dailyLoss / _sessionStartingEquity * 100m);
        }

        if (!broker.Position.HasPosition || broker.Position.AverageBuyPrice <= 0m)
        {
            return null;
        }

        PositionStopLossAssessment stopLoss = PositionStopLossCalculator.Evaluate(
            _settings,
            broker.Position,
            mark);

        if (!stopLoss.IsTriggered)
        {
            return null;
        }

        return new(
            latest.SourceTimestampUtc,
            StrategySignalKind.StopLoss,
            "STOP LOSS",
            1m,
            [_settings.StopLossBasis is StopLossBasis.TotalPositionLossAmount
                ? $"Position loss ${stopLoss.UnrealizedLoss:0.00} reached the ${stopLoss.LossLimit:0.00} stop."
                : $"Price decline {stopLoss.DeclinePercentage:0.00}% reached the {_settings.StopLossValue:0.00}% purchase-price stop."],
            null,
            0m,
            (mark - broker.Position.AverageBuyPrice) /
            broker.Position.AverageBuyPrice * 100m);
    }

    private string? BrokerBlockReason(LiveBrokerSnapshot broker)
    {
        if (!broker.Account.AgenticAllowed || !broker.Account.IsActive)
        {
            return "The selected Robinhood account is not active and agentic-enabled.";
        }

        if (!broker.Tradability.Tradeable)
        {
            return broker.Tradability.Reason ??
                   $"Robinhood reports {broker.Tradability.Symbol} is not tradeable.";
        }

        if (broker.HasOpenOrder)
        {
            return "An open Robinhood order already exists for this symbol; duplicate submission is blocked.";
        }

        if (broker.Position.HasPosition && broker.Position.AverageBuyPrice <= 0m)
        {
            return "Robinhood returned an open position without a valid average purchase price; LIVE decisions are blocked.";
        }

        return null;
    }

    private string? BuyBlockReason(
        DateTimeOffset now,
        decimal entryPrice,
        LiveBrokerSnapshot broker)
    {
        if (_riskLocked)
        {
            return "Maximum daily loss reached; new LIVE entries are locked for this session.";
        }

        if (broker.Position.HasPosition)
        {
            return "Robinhood already reports an open position for this symbol.";
        }

        if (!_settings.UnlimitedEntries && _entriesToday >= _settings.MaximumEntriesPerDay)
        {
            return "Maximum entries per day reached.";
        }

        if (_lastExitUtc is not null && now - _lastExitUtc < ReentryCooldown)
        {
            return "Waiting for the 30-second re-entry cooldown.";
        }

        if (_lastExitUtc is not null && _lastExitPrice is not > 0m)
        {
            return "The previous LIVE sell price is unavailable; re-entry remains blocked.";
        }

        if (_lastExitPrice is > 0m &&
            !ReentryPriceGate.IsSatisfied(entryPrice, _lastExitPrice.Value))
        {
            decimal movement = ReentryPriceGate.MovementPercentage(
                entryPrice,
                _lastExitPrice.Value);
            return $"Waiting for price to move at least {ReentryPriceGate.MinimumMovementPercentage:0.00}% from the last sell at {_lastExitPrice.Value:C2}; current movement is {movement:0.00}%.";
        }

        return null;
    }

    private LiveTradeEvaluation Hold(DateTimeOffset at, string state, string reason) =>
        new(StrategyDecision.Hold(at, state, reason), null, _riskLocked);

    private LiveTradeEvaluation Block(StrategyDecision decision, string reason) =>
        new(
            decision with
            {
                Signal = StrategySignalKind.Hold,
                State = "RISK BLOCKED",
                Confidence = 0m,
                Reasons = [reason],
            },
            null,
            _riskLocked);

    private decimal DailyLossLimit() => _settings.MaximumDailyLossBasis switch
    {
        AmountBasis.FixedAmount => _settings.MaximumDailyLossValue,
        _ => _sessionStartingEquity * _settings.MaximumDailyLossValue / 100m,
    };

    private static decimal FloorQuantity(decimal quantity) =>
        Math.Floor(quantity * 1_000_000m) / 1_000_000m;
}
