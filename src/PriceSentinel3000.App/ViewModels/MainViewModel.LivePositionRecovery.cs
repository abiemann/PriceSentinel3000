using PriceSentinel3000.Application.LiveTrading;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private InheritedPositionRecovery? _inheritedPositionRecovery;

    internal Func<ExistingLivePositionPrompt, ExistingLivePositionChoice>?
        ExistingLivePositionPrompt { get; set; }

    internal Action<string>? ExistingLivePositionWarning { get; set; }

    private async Task<ExistingLivePositionRecovery?> ResolveExistingPositionAsync(
        Instrument instrument,
        TradingSessionSettings settings,
        LiveBrokerSnapshot initialBroker,
        decimal dailyStartingEquity,
        CancellationToken cancellationToken)
    {
        ValidateRecoverablePosition(initialBroker.Position, instrument);
        MarketQuote quote = await GetLiveStartupQuoteAsync(
            instrument,
            settings,
            cancellationToken);
        PositionStopLossAssessment stopLoss = PositionStopLossCalculator.Evaluate(
            settings,
            initialBroker.Position,
            EstimatedSellPrice(quote));
        UpdateLiveAccount(initialBroker, stopLoss.ExitMark);
        string? monitorBlockReason = BuildMonitorBlockReason(
            settings,
            initialBroker,
            dailyStartingEquity,
            stopLoss);
        string? sellNowBlockReason = BuildSellNowBlockReason(
            initialBroker,
            _timeProvider.GetUtcNow());
        var prompt = new ExistingLivePositionPrompt(
            instrument.Symbol,
            initialBroker.Position.Quantity,
            initialBroker.Position.SharesAvailableForSells,
            initialBroker.Position.AverageBuyPrice,
            stopLoss.ExitMark,
            quote.HasTwoSidedMarket ? "CURRENT BID" : "LAST TRADED PRICE",
            string.IsNullOrWhiteSpace(sellNowBlockReason),
            sellNowBlockReason,
            string.IsNullOrWhiteSpace(monitorBlockReason),
            monitorBlockReason);

        if (ExistingLivePositionPrompt is null)
        {
            AbortLivePositionStartup(
                "POSITION_PROMPT_UNAVAILABLE",
                "LIVE startup stopped because the existing-position confirmation dialog was unavailable.");
            return null;
        }

        ExistingLivePositionChoice choice = ExistingLivePositionPrompt(prompt);
        if (choice is ExistingLivePositionChoice.Cancel)
        {
            AbortLivePositionStartup(
                "POSITION_RECOVERY_CANCELLED",
                $"LIVE startup cancelled; the existing {instrument.Symbol} position was left unchanged.");
            return null;
        }

        if (choice is ExistingLivePositionChoice.SellNow &&
            !prompt.CanSellNow)
        {
            AbortLivePositionStartup(
                "POSITION_SELL_BLOCKED",
                prompt.SellNowBlockReason ??
                "LIVE startup stopped because the existing position could not be sold safely.");
            return null;
        }

        if (choice is ExistingLivePositionChoice.MonitorForProfit &&
            !prompt.CanMonitorForProfit)
        {
            AbortLivePositionStartup(
                "POSITION_MONITOR_BLOCKED",
                prompt.MonitorBlockReason ??
                "LIVE startup stopped because the existing position could not be monitored safely.");
            return null;
        }

        LiveBrokerSnapshot refreshedBroker = await InitializeLiveBrokerAsync(
            instrument,
            cancellationToken);
        if (refreshedBroker.HasOpenOrder)
        {
            AbortLivePositionStartup(
                "POSITION_CHANGED_DURING_CONFIRMATION",
                $"LIVE startup stopped because a Robinhood order appeared for {instrument.Symbol} while the confirmation dialog was open.");
            return null;
        }

        if (!IsSamePosition(initialBroker.Position, refreshedBroker.Position))
        {
            AbortLivePositionStartup(
                "POSITION_CHANGED_DURING_CONFIRMATION",
                $"LIVE startup stopped because the Robinhood {instrument.Symbol} position changed while the confirmation dialog was open. Review it and start again.");
            return null;
        }

        ValidateRecoverablePosition(refreshedBroker.Position, instrument);
        MarketQuote refreshedQuote = await GetLiveStartupQuoteAsync(
            instrument,
            settings,
            cancellationToken);
        PositionStopLossAssessment refreshedStopLoss =
            PositionStopLossCalculator.Evaluate(
                settings,
                refreshedBroker.Position,
                EstimatedSellPrice(refreshedQuote));

        if (choice is ExistingLivePositionChoice.MonitorForProfit)
        {
            string? refreshedMonitorBlock = BuildMonitorBlockReason(
                settings,
                refreshedBroker,
                dailyStartingEquity,
                refreshedStopLoss);
            if (!string.IsNullOrWhiteSpace(refreshedMonitorBlock))
            {
                ExistingLivePositionWarning?.Invoke(refreshedMonitorBlock);
                AbortLivePositionStartup(
                    "POSITION_MONITOR_BLOCKED",
                    $"LIVE startup stopped after refreshing Robinhood: {refreshedMonitorBlock}");
                return null;
            }
        }
        else
        {
            string? refreshedSellBlock = BuildSellNowBlockReason(
                refreshedBroker,
                _timeProvider.GetUtcNow());
            if (!string.IsNullOrWhiteSpace(refreshedSellBlock))
            {
                AbortLivePositionStartup(
                    "POSITION_SELL_BLOCKED",
                    $"LIVE startup stopped after refreshing Robinhood: {refreshedSellBlock}");
                return null;
            }
        }

        return new(choice, refreshedBroker, refreshedQuote);
    }

    private async Task<MarketQuote> GetLiveStartupQuoteAsync(
        Instrument instrument,
        TradingSessionSettings settings,
        CancellationToken cancellationToken)
    {
        DateTimeOffset observedAt = _timeProvider.GetUtcNow();
        var request = new MarketDataRequest(
            instrument,
            TimeSpan.FromSeconds(settings.QuotePollingSeconds),
            TimeSpan.FromMinutes(settings.BufferMinutes));
        MarketQuote quote = await _marketDataSource.GetQuoteAsync(
            request,
            observedAt,
            cancellationToken);

        if (!IsFreshObservation(quote))
        {
            throw new InvalidOperationException(
                "Robinhood did not return a fresh quote for the existing position; LIVE startup remains disarmed.");
        }

        _ = EstimatedSellPrice(quote);
        return quote;
    }

    private static decimal EstimatedSellPrice(MarketQuote quote)
    {
        decimal price = quote.HasTwoSidedMarket ? quote.Bid : quote.Last;
        return price > 0m
            ? price
            : throw new InvalidOperationException(
                "Robinhood did not return a usable sell-side price for the existing position; LIVE startup remains disarmed.");
    }

    private static void ValidateRecoverablePosition(
        BrokerPosition position,
        Instrument instrument)
    {
        if (!string.Equals(
                position.Symbol,
                instrument.Symbol,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Robinhood returned a position for a different symbol; LIVE startup remains disarmed.");
        }

        if (position.Quantity <= 0m)
        {
            throw new InvalidOperationException(
                "PriceSentinel can recover only a positive long position; LIVE startup remains disarmed.");
        }

        if (position.AverageBuyPrice <= 0m)
        {
            throw new InvalidOperationException(
                "Robinhood returned the existing position without a valid average purchase price; LIVE startup remains disarmed.");
        }

        if (position.SharesAvailableForSells < position.Quantity)
        {
            throw new InvalidOperationException(
                $"Only {position.SharesAvailableForSells:0.######} of {position.Quantity:0.######} {instrument.Symbol} shares are available to sell. PriceSentinel will not manage a partial inherited position; resolve held shares in Robinhood first.");
        }
    }

    private static bool IsSamePosition(
        BrokerPosition expected,
        BrokerPosition actual) =>
        string.Equals(
            expected.Symbol,
            actual.Symbol,
            StringComparison.OrdinalIgnoreCase) &&
        expected.Quantity == actual.Quantity &&
        expected.AverageBuyPrice == actual.AverageBuyPrice &&
        expected.SharesAvailableForSells == actual.SharesAvailableForSells &&
        expected.SharesHeldForSells == actual.SharesHeldForSells;

    private string? BuildSellNowBlockReason(
        LiveBrokerSnapshot broker,
        DateTimeOffset nowUtc)
    {
        if (!IsRegularEquityMarketHours(nowUtc))
        {
            return "Sell Now is available only during the app's 9:30 AM-4:00 PM ET regular-hours LIVE window.";
        }

        if (!broker.Tradability.FractionalTradeable &&
            broker.Position.Quantity != Math.Floor(broker.Position.Quantity))
        {
            return $"Sell Now is unavailable because Robinhood does not allow fractional trading for {broker.Position.Symbol}.";
        }

        if (broker.Position.Quantity != BrokerOrderQuantity(broker.Position.Quantity))
        {
            return "Sell Now is unavailable because the inherited share quantity exceeds Robinhood's six-decimal order precision.";
        }

        return null;
    }

    private static string? BuildMonitorBlockReason(
        TradingSessionSettings settings,
        LiveBrokerSnapshot broker,
        decimal dailyStartingEquity,
        PositionStopLossAssessment stopLoss)
    {
        var reasons = new List<string>();
        if (stopLoss.IsTriggered)
        {
            reasons.Add(settings.StopLossBasis is StopLossBasis.TotalPositionLossAmount
                ? $"Profit monitoring is blocked because the current estimated loss {stopLoss.UnrealizedLoss:C2} already reaches the configured {stopLoss.LossLimit:C2} stop. Cancel, adjust Stop loss, and start again—or explicitly choose Sell Now."
                : $"Profit monitoring is blocked because the current estimated sell price {stopLoss.ExitMark:C2} is at or below the configured stop price {stopLoss.TriggerPrice:C2} ({settings.StopLossValue:0.##}% below the {broker.Position.AverageBuyPrice:C2} average cost). Cancel, adjust Stop loss, and start again—or explicitly choose Sell Now.");
        }

        if (!broker.Tradability.FractionalTradeable &&
            broker.Position.Quantity != Math.Floor(broker.Position.Quantity))
        {
            reasons.Add(
                $"Profit monitoring is blocked because Robinhood does not allow PriceSentinel to submit the fractional {broker.Position.Symbol} exit required for this position.");
        }

        if (broker.Position.Quantity != BrokerOrderQuantity(broker.Position.Quantity))
        {
            reasons.Add(
                "Profit monitoring is blocked because the inherited share quantity exceeds Robinhood's six-decimal order precision.");
        }

        decimal dailyLoss = Math.Max(
            0m,
            dailyStartingEquity - broker.Portfolio.TotalValue);
        decimal dailyLimit = settings.MaximumDailyLossBasis switch
        {
            AmountBasis.FixedAmount => settings.MaximumDailyLossValue,
            _ => dailyStartingEquity * settings.MaximumDailyLossValue / 100m,
        };
        if (dailyLoss >= dailyLimit)
        {
            reasons.Add(
                $"Profit monitoring is blocked because today's account drawdown {dailyLoss:C2} already reaches the configured {dailyLimit:C2} daily-loss limit; otherwise the position would be liquidated immediately.");
        }

        return reasons.Count == 0
            ? null
            : string.Join(" ", reasons);
    }

    private void AbortLivePositionStartup(string outcome, string message)
    {
        AddActivity(message, "WARNING");
        StopActiveSession(outcome, message, keepRobinhoodConnected: true);
    }

    private async Task<bool> SellExistingPositionNowAsync(
        Instrument instrument,
        TradingSessionSettings settings,
        ExistingLivePositionRecovery recovery,
        CancellationToken cancellationToken)
    {
        if (_liveAccount is null || _activeSession is null)
        {
            throw new InvalidOperationException(
                "The LIVE session was not initialized for the requested position sale.");
        }

        MarketQuote quote = await GetLiveStartupQuoteAsync(
            instrument,
            settings,
            cancellationToken);
        LiveBrokerSnapshot broker = await CaptureLiveBrokerAsync(
            instrument,
            cancellationToken);
        if (broker.HasOpenOrder ||
            !IsSamePosition(recovery.Broker.Position, broker.Position))
        {
            return StopInheritedPositionRecovery(
                "POSITION_CHANGED_BEFORE_SELL",
                $"LIVE startup stopped because the Robinhood {instrument.Symbol} position or open-order state changed before the requested sale. No new PriceSentinel order was submitted.");
        }

        string? sellBlock = BuildSellNowBlockReason(
            broker,
            _timeProvider.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(sellBlock))
        {
            return StopInheritedPositionRecovery(
                "POSITION_SELL_BLOCKED",
                sellBlock);
        }

        UpdateLiveAccount(broker, EstimatedSellPrice(quote));
        decimal quantity = BrokerOrderQuantity(broker.Position.Quantity);
        var intent = new BrokerOrderIntent(
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            instrument.Symbol,
            BrokerOrderSide.Sell,
            quantity,
            "USER REQUESTED EXISTING POSITION LIQUIDATION");
        if (!RegisterInheritedPositionExitIntent(intent))
        {
            return false;
        }
        LiveOrderOperationResult operation =
            await _liveOrderCoordinator.ExecuteAsync(
                _liveAccount,
                _activeSession.Id,
                instrument,
                quote,
                intent,
                cancellationToken);
        await ApplyLiveOrderOperationAsync(
            operation,
            instrument,
            EstimatedSellPrice(quote),
            cancellationToken);
        return await HandleInheritedPositionOrderResultAsync(
            operation,
            instrument,
            cancellationToken);
    }

    private void BeginInheritedPositionRecovery(BrokerPosition position) =>
        _inheritedPositionRecovery = new(position, null);

    private void ClearInheritedPositionRecovery() =>
        _inheritedPositionRecovery = null;

    private bool RegisterInheritedPositionExitIntent(BrokerOrderIntent intent)
    {
        if (_inheritedPositionRecovery is null)
        {
            return true;
        }

        if (intent.Side is not BrokerOrderSide.Sell)
        {
            return StopInheritedPositionRecovery(
                "INHERITED_POSITION_BUY_BLOCKED",
                "LIVE trading stopped because an entry order was proposed before the inherited Robinhood position had a confirmed exit.");
        }

        _inheritedPositionRecovery = _inheritedPositionRecovery with
        {
            ExitIntentReference = intent.ClientReferenceId,
        };
        return true;
    }

    private bool ValidateInheritedPositionSnapshot(LiveBrokerSnapshot broker)
    {
        if (_inheritedPositionRecovery is null)
        {
            return true;
        }

        if (!broker.HasOpenOrder &&
            IsSamePosition(
                _inheritedPositionRecovery.ExpectedPosition,
                broker.Position))
        {
            return true;
        }

        return StopInheritedPositionRecovery(
            "INHERITED_POSITION_CHANGED",
            $"LIVE trading stopped because the inherited {broker.Position.Symbol} position changed or disappeared without a confirmed PriceSentinel exit. Review the position and any orders in Robinhood before starting again.");
    }

    private async Task<bool> HandleInheritedPositionOrderResultAsync(
        LiveOrderOperationResult operation,
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        InheritedPositionRecovery? recovery = _inheritedPositionRecovery;
        if (recovery is null)
        {
            return true;
        }

        if (operation.TerminalOrder is not BrokerOrderSnapshot terminalOrder)
        {
            if (!string.IsNullOrWhiteSpace(operation.PendingState))
            {
                AddActivity(
                    $"The inherited {instrument.Symbol} exit remains active at Robinhood; all new entries stay blocked until its final state and a flat broker position are confirmed.",
                    "WARNING");
                return true;
            }

            return StopInheritedPositionRecovery(
                "INHERITED_POSITION_EXIT_NOT_ACCEPTED",
                $"Robinhood did not accept the inherited {instrument.Symbol} exit. LIVE trading was stopped and the position was left for review.");
        }

        if (operation.Intent is not BrokerOrderIntent intent ||
            intent.Side is not BrokerOrderSide.Sell ||
            recovery.ExitIntentReference is not Guid expectedReference ||
            intent.ClientReferenceId != expectedReference)
        {
            return StopInheritedPositionRecovery(
                "INHERITED_POSITION_EXIT_MISMATCH",
                $"LIVE trading stopped because the terminal Robinhood order did not match the inherited {instrument.Symbol} exit intent.");
        }

        decimal expectedQuantity = recovery.ExpectedPosition.Quantity;
        if (terminalOrder.State is not BrokerOrderState.Filled ||
            terminalOrder.RequestedQuantity != expectedQuantity ||
            terminalOrder.FilledQuantity != expectedQuantity)
        {
            return StopInheritedPositionRecovery(
                "INHERITED_POSITION_EXIT_INCOMPLETE",
                $"The inherited {instrument.Symbol} exit ended {terminalOrder.State} with {terminalOrder.FilledQuantity:0.######} of {expectedQuantity:0.######} shares filled. LIVE trading was stopped; verify the remaining position in Robinhood.");
        }

        LiveBrokerSnapshot? confirmedFlat = await ConfirmBrokerFlatAsync(
            instrument,
            cancellationToken);
        if (confirmedFlat is null)
        {
            return StopInheritedPositionRecovery(
                "INHERITED_POSITION_NOT_FLAT",
                $"Robinhood reported the inherited {instrument.Symbol} exit as filled, but PriceSentinel could not confirm a flat position with no open order. LIVE trading was stopped; verify Robinhood immediately.");
        }

        UpdateLiveAccount(confirmedFlat, 0m);
        _liveExecutionEngine?.ConfirmInheritedPositionClosed();
        ClearInheritedPositionRecovery();
        AddActivity(
            $"Robinhood confirmed the inherited {instrument.Symbol} position is fully sold and flat; LIVE Trader may now consider new entries.");
        return true;
    }

    private async Task<LiveBrokerSnapshot?> ConfirmBrokerFlatAsync(
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            LiveBrokerSnapshot broker = await CaptureLiveBrokerAsync(
                instrument,
                cancellationToken);
            if (!broker.Position.HasPosition && !broker.HasOpenOrder)
            {
                return broker;
            }

            if (attempt < 7)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    _timeProvider,
                    cancellationToken);
            }
        }

        return null;
    }

    private bool StopInheritedPositionRecovery(string outcome, string message)
    {
        ExistingLivePositionWarning?.Invoke(message);
        AbortLivePositionStartup(outcome, message);
        return false;
    }

    private static decimal BrokerOrderQuantity(decimal quantity) =>
        Math.Floor(quantity * 1_000_000m) / 1_000_000m;

    private sealed record ExistingLivePositionRecovery(
        ExistingLivePositionChoice Choice,
        LiveBrokerSnapshot Broker,
        MarketQuote Quote);

    private sealed record InheritedPositionRecovery(
        BrokerPosition ExpectedPosition,
        Guid? ExitIntentReference);
}
