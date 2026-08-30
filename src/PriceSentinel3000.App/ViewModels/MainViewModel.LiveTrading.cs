using PriceSentinel3000.Application.LiveTrading;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<LiveBrokerSnapshot> InitializeLiveBrokerAsync(
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        CancelSymbolTradabilityRefresh();
        ILiveBrokerGateway gateway = _liveBrokerGateway ??
            throw new InvalidOperationException("LIVE broker execution is unavailable.");
        _liveAccount = await gateway.GetAgenticAccountAsync(cancellationToken);
        BrokerPortfolio portfolio = await gateway.GetPortfolioAsync(
            _liveAccount.AccountNumber,
            cancellationToken);
        BrokerPosition position = await gateway.GetPositionAsync(
            _liveAccount.AccountNumber,
            instrument,
            cancellationToken);
        _liveTradability = await gateway.GetTradabilityAsync(
            _liveAccount.AccountNumber,
            _liveAccount.AccountType,
            instrument,
            cancellationToken);
        _tradabilityAccount = _liveAccount;
        SetSymbolTradability(_liveTradability);
        IReadOnlyList<BrokerOrderSnapshot> orders = await gateway.GetOpenOrdersAsync(
            _liveAccount.AccountNumber,
            instrument,
            cancellationToken);

        if (portfolio.TotalValue <= 0m)
        {
            throw new InvalidOperationException(
                "Robinhood returned an invalid account value; LIVE execution remains disarmed.");
        }

        if (!_liveTradability.Tradeable)
        {
            throw new InvalidOperationException(
                _liveTradability.Reason ??
                $"Robinhood reports {instrument.Symbol} is not tradeable.");
        }

        return new(
            _liveAccount,
            portfolio,
            position,
            _liveTradability,
            orders,
            _timeProvider.GetUtcNow());
    }

    private async Task<LiveBrokerSnapshot> CaptureLiveBrokerAsync(
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ILiveBrokerGateway gateway = _liveBrokerGateway ??
            throw new InvalidOperationException("LIVE broker execution is unavailable.");
        BrokerAccount account = _liveAccount ??
            throw new InvalidOperationException("No agentic Robinhood account is selected.");
        EquityTradability tradability = _liveTradability ??
            throw new InvalidOperationException("Equity tradability was not verified.");
        BrokerPortfolio portfolio = await gateway.GetPortfolioAsync(
            account.AccountNumber,
            cancellationToken);
        BrokerPosition position = await gateway.GetPositionAsync(
            account.AccountNumber,
            instrument,
            cancellationToken);
        IReadOnlyList<BrokerOrderSnapshot> orders = await gateway.GetOpenOrdersAsync(
            account.AccountNumber,
            instrument,
            cancellationToken);
        return new(
            account,
            portfolio,
            position,
            tradability,
            orders,
            _timeProvider.GetUtcNow());
    }

    private async Task ProcessLiveObservationAsync(
        MarketQuote trigger,
        CancellationToken cancellationToken)
    {
        if (_ringBuffer is null ||
            _activeSession is null ||
            _liveExecutionEngine is null ||
            _liveAccount is null)
        {
            return;
        }

        LiveOrderOperationResult reconciliation =
            await _liveOrderCoordinator.ReconcileActiveAsync(
                _liveAccount,
                _activeSession.Id,
                _ringBuffer.Instrument,
                cancellationToken);
        if (reconciliation.Handled)
        {
            await ApplyLiveOrderOperationAsync(
                reconciliation,
                _ringBuffer.Instrument,
                trigger.Last,
                cancellationToken);
            await HandleInheritedPositionOrderResultAsync(
                reconciliation,
                _ringBuffer.Instrument,
                cancellationToken);
            return;
        }

        if (!IsFreshObservation(trigger))
        {
            _strategyStateLabel = "MARKET CLOSED";
            _strategyMessage =
                "The newest Robinhood venue timestamp is stale; LIVE decisions and new orders are paused.";
            _strategyMetrics = "RSI --  |  MOM --  |  CONF --";
            NotifyStrategyProperties();
            return;
        }

        LiveBrokerSnapshot broker = await CaptureLiveBrokerAsync(
            _ringBuffer.Instrument,
            cancellationToken);
        UpdateLiveAccount(broker, trigger.Last);
        if (!ValidateInheritedPositionSnapshot(broker))
        {
            return;
        }

        LiveTradeEvaluation evaluation = _liveExecutionEngine.Evaluate(
            _ringBuffer.Snapshot(),
            broker);
        _journal.AppendDecision(_activeSession.Id, evaluation.Decision);
        UpdateStrategyDecision(evaluation.Decision);

        BrokerOrderIntent? intent = evaluation.Intent;
        if (intent is null)
        {
            return;
        }

        if (!LiveArmed)
        {
            AddActivity(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} signal ignored because broker execution is disarmed.",
                "WARNING");
            return;
        }

        if (!IsRegularEquityMarketHours(_timeProvider.GetUtcNow()))
        {
            _strategyStateLabel = "MARKET HOURS ONLY";
            _strategyMessage =
                "A confirmed signal occurred outside regular equity hours; no LIVE order was submitted.";
            NotifyStrategyProperties();
            AddActivity(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} blocked outside 9:30 AM-4:00 PM ET regular equity hours.",
                "WARNING");
            return;
        }

        if (!broker.Tradability.FractionalTradeable &&
            intent.Quantity != Math.Floor(intent.Quantity))
        {
            AddActivity(
                $"LIVE order blocked because Robinhood does not allow fractional trading for {intent.Symbol}.",
                "WARNING");
            return;
        }

        if (!RegisterInheritedPositionExitIntent(intent))
        {
            return;
        }

        LiveOrderOperationResult execution =
            await _liveOrderCoordinator.ExecuteAsync(
                _liveAccount,
                _activeSession.Id,
                _ringBuffer.Instrument,
                trigger,
                intent,
                cancellationToken);
        await ApplyLiveOrderOperationAsync(
            execution,
            _ringBuffer.Instrument,
            trigger.Last,
            cancellationToken);
        await HandleInheritedPositionOrderResultAsync(
            execution,
            _ringBuffer.Instrument,
            cancellationToken);
    }

    private async Task ApplyLiveOrderOperationAsync(
        LiveOrderOperationResult operation,
        Instrument instrument,
        decimal mark,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(operation.PendingState))
        {
            _strategyStateLabel = "ORDER PENDING";
            _strategyMessage = operation.PendingState;
            NotifyStrategyProperties();
        }

        if (operation.TerminalOrder is not BrokerOrderSnapshot terminalOrder)
        {
            return;
        }

        _liveExecutionEngine?.ObserveTerminalOrder(terminalOrder);
        if (terminalOrder.State is BrokerOrderState.Filled &&
            operation.Intent is not null &&
            operation.TriggerTimestampUtc is not null)
        {
            _tradeMarkers[operation.TriggerTimestampUtc.Value] =
                operation.Intent.Side is BrokerOrderSide.Buy
                    ? ChartTradeMarker.Buy
                    : ChartTradeMarker.Sell;
        }

        if (terminalOrder.FilledQuantity <= 0m)
        {
            return;
        }

        LiveBrokerSnapshot reconciled = await CaptureLiveBrokerAsync(
            instrument,
            cancellationToken);
        UpdateLiveAccount(reconciled, mark);
    }

    private void UpdateLiveAccount(LiveBrokerSnapshot broker, decimal mark)
    {
        _paperBuyingPower = broker.Portfolio.BuyingPower;
        _paperEquity = broker.Portfolio.TotalValue;
        _paperPositionQuantity = broker.Position.Quantity;
        _paperAveragePrice = broker.Position.AverageBuyPrice;
        _paperUnrealizedProfitLoss = broker.Position.HasPosition && mark > 0m
            ? broker.Position.Quantity * (mark - broker.Position.AverageBuyPrice)
            : 0m;
        _paperEntries = _liveExecutionEngine?.EntriesToday ?? 0;
        OnPropertyChanged(nameof(AccountBalanceValue));
        OnPropertyChanged(nameof(BuyingPowerDisplay));
        OnPropertyChanged(nameof(AccountEquityDisplay));
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProfitLossDisplay));
        OnPropertyChanged(nameof(EntriesDisplay));
    }

    private void DisarmLiveExecution(string reason)
    {
        if (SelectedMode is TradingMode.Live)
        {
            _modeState = _modeState.ActivateLiveDisarmed();
            NotifyModeProperties();
        }

        StatusMessage = reason;
    }

    private static DateTimeOffset GetEasternTradingDayStartUtc(DateTimeOffset nowUtc)
    {
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTimeOffset easternNow = TimeZoneInfo.ConvertTime(nowUtc, eastern);
        DateTime easternDate = DateTime.SpecifyKind(easternNow.Date, DateTimeKind.Unspecified);
        TimeSpan offset = eastern.GetUtcOffset(easternDate);
        return new DateTimeOffset(easternDate, offset).ToUniversalTime();
    }

    private static bool IsRegularEquityMarketHours(DateTimeOffset nowUtc)
    {
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTimeOffset easternNow = TimeZoneInfo.ConvertTime(nowUtc, eastern);
        TimeOnly time = TimeOnly.FromDateTime(easternNow.DateTime);
        return easternNow.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
               time >= new TimeOnly(9, 30) &&
               time < new TimeOnly(16, 0);
    }
}
