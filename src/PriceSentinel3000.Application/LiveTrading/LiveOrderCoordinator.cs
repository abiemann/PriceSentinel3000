using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.LiveTrading;

public sealed record LiveOrderActivity(string Message, string Level = "INFO");

public sealed record LiveOrderOperationResult(
    bool Handled,
    BrokerOrderIntent? Intent = null,
    BrokerOrderSnapshot? TerminalOrder = null,
    DateTimeOffset? TriggerTimestampUtc = null,
    string? PendingState = null);

public sealed class LiveOrderCoordinator : IDisposable
{
    private readonly ILiveBrokerGateway _gateway;
    private readonly ITradingJournal _journal;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _orderGate = new(1, 1);
    private BrokerOrderSnapshot? _activeOrder;
    private BrokerOrderIntent? _activeIntent;
    private BrokerOrderReview? _activeReview;
    private DateTimeOffset? _activeTriggerTimestamp;
    private bool _disposed;

    public LiveOrderCoordinator(
        ILiveBrokerGateway gateway,
        ITradingJournal journal,
        TimeProvider? timeProvider = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<LiveOrderActivity>? Activity;
    public event Action<string>? DisarmRequested;

    public BrokerOrderSnapshot? ActiveOrder => _activeOrder;
    public bool HasActiveContext => _activeIntent is not null || _activeOrder is not null;

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HasActiveContext)
        {
            throw new InvalidOperationException(
                "The unresolved LIVE order context must be reconciled or cancelled before another session can start.");
        }

        ClearActiveContext();
    }

    public async Task<LiveOrderOperationResult> ExecuteAsync(
        BrokerAccount account,
        Guid sessionId,
        Instrument instrument,
        MarketQuote trigger,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(intent);

        if (!await _orderGate.WaitAsync(0, cancellationToken))
        {
            Publish("A LIVE order workflow is already active; duplicate signal ignored.", "WARNING");
            return new(true);
        }

        try
        {
            if (HasActiveContext)
            {
                const string pendingState =
                    "A LIVE order context is already active; all new LIVE orders are blocked until reconciliation or cancellation completes.";
                Publish(pendingState, "WARNING");
                return new(true, PendingState: pendingState);
            }

            return await ReviewPlaceAndReconcileAsync(
                account,
                sessionId,
                instrument,
                trigger,
                intent,
                cancellationToken);
        }
        finally
        {
            _orderGate.Release();
        }
    }

    public async Task<LiveOrderOperationResult> ReconcileActiveAsync(
        BrokerAccount account,
        Guid sessionId,
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeOrder?.IsOpen is not true)
        {
            return new(false);
        }

        if (_activeIntent is null || _activeReview is null)
        {
            RequestDisarm(
                "An active LIVE order could not be reconciled from memory. Verify Robinhood before restarting PriceSentinel.");
            return new(true);
        }

        BrokerOrderSnapshot order = await _gateway.GetOrderAsync(
            account.AccountNumber,
            _activeOrder.BrokerOrderId,
            cancellationToken);
        order = order with
        {
            ClientReferenceId = _activeIntent.ClientReferenceId,
        };
        _activeOrder = order;
        AppendEvent(
            sessionId,
            instrument,
            order.IsTerminal ? "TERMINAL" : "BROKER_STATE",
            _activeIntent,
            _activeReview,
            order);

        if (!order.IsTerminal)
        {
            return new(
                true,
                PendingState: $"Robinhood order {order.State} is still active; all new LIVE orders are blocked.");
        }

        BrokerOrderIntent intent = _activeIntent;
        DateTimeOffset? triggerTimestamp = _activeTriggerTimestamp;
        ClearActiveContext();

        if (order.State is BrokerOrderState.Filled)
        {
            Publish(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} reached FILLED during reconciliation: {order.FilledQuantity:0.######} {intent.Symbol} @ {(order.AveragePrice ?? 0m):C2}.");
        }
        else
        {
            Publish(
                $"LIVE order ended {order.State}: {order.RejectionReason ?? "no broker reason supplied"}. Execution has been disarmed.",
                "ERROR");
            RequestDisarm(
                "A Robinhood order did not fill successfully; inspect the journal before re-arming.");
        }

        return new(true, intent, order, triggerTimestamp);
    }

    public async Task<LiveOrderOperationResult> CancelActiveAsync(
        BrokerAccount? account,
        Guid? sessionId,
        Instrument instrument,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BrokerOrderSnapshot? order = _activeOrder;

        if (account is null || (_activeIntent is null && order is null))
        {
            return new(false);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            if (order is null || string.IsNullOrWhiteSpace(order.BrokerOrderId))
            {
                order = await FindActiveOrderByReferenceAsync(
                    account,
                    instrument,
                    attempts: 5,
                    timeout.Token);
            }

            if (order is null)
            {
                Publish(
                    "STOP could not locate the in-flight order by its idempotent reference. Verify Robinhood immediately.",
                    "ERROR");
                return new(false);
            }

            _activeOrder = order;
            if (order.IsTerminal)
            {
                RecordStoppedOrderState(sessionId, instrument, order, "TERMINAL");
                BrokerOrderIntent? terminalIntent = _activeIntent;
                DateTimeOffset? triggerTimestamp = _activeTriggerTimestamp;
                ClearActiveContext();
                Publish(
                    $"STOP found the Robinhood order already {order.State}; no cancellation request was sent. Verify the resulting position.",
                    order.State is BrokerOrderState.Filled ? "WARNING" : "INFO");
                return new(true, terminalIntent, order, triggerTimestamp);
            }

            await _gateway.CancelOrderAsync(
                account.AccountNumber,
                order.BrokerOrderId,
                timeout.Token);
            RecordStoppedOrderState(sessionId, instrument, order, "CANCEL_REQUESTED");
            Publish(
                "STOP requested cancellation of the active Robinhood order; waiting briefly for its final broker state.",
                "WARNING");

            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    _timeProvider,
                    timeout.Token);
                order = await _gateway.GetOrderAsync(
                    account.AccountNumber,
                    order.BrokerOrderId,
                    timeout.Token);
                _activeOrder = order;
                RecordStoppedOrderState(
                    sessionId,
                    instrument,
                    order,
                    order.IsTerminal ? "TERMINAL" : "CANCEL_RECONCILE");

                if (!order.IsTerminal)
                {
                    continue;
                }

                BrokerOrderIntent? terminalIntent = _activeIntent;
                DateTimeOffset? triggerTimestamp = _activeTriggerTimestamp;
                ClearActiveContext();
                if (order.FilledQuantity > 0m)
                {
                    Publish(
                        $"STOP reconciliation found {order.FilledQuantity:0.######} shares filled before the order reached {order.State}. Check the resulting Robinhood position immediately.",
                        "ERROR");
                }
                else
                {
                    Publish($"Robinhood order reached final state {order.State} after STOP.");
                }

                return new(true, terminalIntent, order, triggerTimestamp);
            }

            Publish(
                $"Robinhood accepted cancellation, but the order is still {order.State}. Verify its final state and any position in Robinhood.",
                "WARNING");
            return new(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            Publish(
                $"STOP could not confirm Robinhood order cancellation: {exception.Message}. Verify Robinhood immediately.",
                "ERROR");
            return new(false);
        }
    }

    private async Task<LiveOrderOperationResult> ReviewPlaceAndReconcileAsync(
        BrokerAccount account,
        Guid sessionId,
        Instrument instrument,
        MarketQuote trigger,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken)
    {
        AppendEvent(sessionId, instrument, "INTENT_CREATED", intent, null, null);
        Publish(
            $"Reviewing LIVE {intent.Side.ToString().ToUpperInvariant()} {intent.Quantity:0.######} {intent.Symbol} with Robinhood. Reason: {intent.Reason}.");

        BrokerOrderReview review = await _gateway.ReviewOrderAsync(
            account.AccountNumber,
            intent,
            cancellationToken);
        AppendEvent(
            sessionId,
            instrument,
            review.Accepted ? "REVIEW_ACCEPTED" : "REVIEW_BLOCKED",
            intent,
            review,
            null);

        if (!string.IsNullOrWhiteSpace(review.MarketDataDisclosure))
        {
            Publish($"Robinhood market data for LIVE review: {review.MarketDataDisclosure}");
        }

        if (!review.Accepted)
        {
            string reason = review.Blockers.FirstOrDefault() ??
                            "Robinhood did not accept the order review.";
            Publish($"LIVE order review blocked: {reason}", "WARNING");
            return new(true);
        }

        decimal triggerPrice = intent.Side is BrokerOrderSide.Buy
            ? trigger.HasTwoSidedMarket ? trigger.Ask : trigger.Last
            : trigger.HasTwoSidedMarket ? trigger.Bid : trigger.Last;
        decimal? reviewedPrice = intent.Side is BrokerOrderSide.Buy
            ? review.AskPrice ?? review.LastPrice
            : review.BidPrice ?? review.LastPrice;

        if (reviewedPrice is null or <= 0m || triggerPrice <= 0m)
        {
            Publish(
                "LIVE order blocked because Robinhood did not return a valid reviewed execution-side price.",
                "WARNING");
            return new(true);
        }

        if (Math.Abs(reviewedPrice.Value - triggerPrice) / triggerPrice > 0.005m)
        {
            Publish(
                "LIVE order blocked because Robinhood's reviewed price moved more than 0.50% from the triggering quote.",
                "WARNING");
            return new(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _activeIntent = intent;
        _activeReview = review;
        _activeTriggerTimestamp = trigger.SourceTimestampUtc;
        BrokerOrderSnapshot placed;

        try
        {
            placed = await _gateway.PlaceOrderAsync(
                account.AccountNumber,
                intent,
                cancellationToken);
        }
        catch (Exception firstException) when (firstException is not OperationCanceledException)
        {
            Publish(
                "Robinhood placement response was uncertain; retrying once with the same idempotent reference ID.",
                "WARNING");
            await Task.Delay(
                TimeSpan.FromSeconds(1),
                _timeProvider,
                cancellationToken);
            try
            {
                placed = await _gateway.PlaceOrderAsync(
                    account.AccountNumber,
                    intent,
                    cancellationToken);
            }
            catch (Exception secondException) when (secondException is not OperationCanceledException)
            {
                BrokerOrderSnapshot? recovered = await FindActiveOrderByReferenceAsync(
                    account,
                    instrument,
                    attempts: 3,
                    cancellationToken);
                if (recovered is null)
                {
                    AppendEvent(
                        sessionId,
                        instrument,
                        "PLACEMENT_UNCERTAIN",
                        intent,
                        review,
                        null);
                    RequestDisarm(
                        "Robinhood did not confirm whether the LIVE order was accepted. Verify Robinhood immediately before restarting.");
                    throw new InvalidOperationException(
                        "Robinhood placement remained uncertain after an idempotent retry; verify Robinhood immediately.",
                        secondException);
                }

                placed = recovered;
                Publish(
                    "Recovered the Robinhood order by its idempotent reference after a lost placement response.",
                    "WARNING");
            }
        }

        placed = placed with { ClientReferenceId = intent.ClientReferenceId };
        ValidatePlacedOrder(placed, intent);
        _activeOrder = placed;
        AppendEvent(sessionId, instrument, "SUBMITTED", intent, review, placed);
        Publish(
            $"LIVE {intent.Side.ToString().ToUpperInvariant()} submitted to Robinhood; state {placed.State.ToString().ToUpperInvariant()}.");

        BrokerOrderState priorState = placed.State;

        for (int attempt = 0; attempt < 30 && !placed.IsTerminal; attempt++)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(1),
                _timeProvider,
                cancellationToken);
            placed = await _gateway.GetOrderAsync(
                account.AccountNumber,
                placed.BrokerOrderId,
                cancellationToken);
            placed = placed with { ClientReferenceId = intent.ClientReferenceId };
            _activeOrder = placed;

            if (placed.State == priorState && attempt % 5 != 4)
            {
                continue;
            }

            AppendEvent(sessionId, instrument, "BROKER_STATE", intent, review, placed);
            priorState = placed.State;
        }

        if (!placed.IsTerminal)
        {
            Publish(
                $"Robinhood order remains {placed.State}; PriceSentinel will block duplicate orders and continue reconciliation.",
                "WARNING");
            return new(true, PendingState: $"Robinhood order {placed.State} is still active; all new LIVE orders are blocked.");
        }

        AppendEvent(sessionId, instrument, "TERMINAL", intent, review, placed);
        ClearActiveContext();

        if (placed.State is BrokerOrderState.Filled)
        {
            Publish(
                $"LIVE {intent.Side.ToString().ToUpperInvariant()} filled {placed.FilledQuantity:0.######} {intent.Symbol} @ {(placed.AveragePrice ?? reviewedPrice ?? triggerPrice):C2}.");
        }
        else
        {
            Publish(
                $"LIVE order ended {placed.State}: {placed.RejectionReason ?? "no broker reason supplied"}. Execution has been disarmed.",
                "ERROR");
            RequestDisarm(
                "A Robinhood order did not fill successfully; inspect the journal before re-arming.");
        }

        return new(true, intent, placed, trigger.SourceTimestampUtc);
    }

    private async Task<BrokerOrderSnapshot?> FindActiveOrderByReferenceAsync(
        BrokerAccount account,
        Instrument instrument,
        int attempts,
        CancellationToken cancellationToken)
    {
        if (_activeIntent is null)
        {
            return null;
        }

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            BrokerOrderSnapshot? recovered =
                await _gateway.FindOrderByClientReferenceAsync(
                    account.AccountNumber,
                    instrument,
                    _activeIntent.ClientReferenceId,
                    cancellationToken);
            if (recovered?.ClientReferenceId == Guid.Empty ||
                recovered?.ClientReferenceId != _activeIntent.ClientReferenceId)
            {
                recovered = null;
            }

            if (recovered is null)
            {
                IReadOnlyList<BrokerOrderSnapshot> recentOrders =
                    await _gateway.GetOrdersCreatedSinceAsync(
                        account.AccountNumber,
                        _activeIntent.CreatedAtUtc.AddSeconds(-5),
                        cancellationToken);
                BrokerOrderSnapshot[] exactMatches =
                [
                    .. recentOrders
                    .Where(order =>
                        order.ClientReferenceId != Guid.Empty &&
                        order.ClientReferenceId == _activeIntent.ClientReferenceId &&
                        string.Equals(
                            order.Symbol,
                            _activeIntent.Symbol,
                            StringComparison.OrdinalIgnoreCase) &&
                        order.Side == _activeIntent.Side &&
                        order.RequestedQuantity == _activeIntent.Quantity)
                    .GroupBy(
                        order => string.IsNullOrWhiteSpace(order.BrokerOrderId)
                            ? $"client:{order.ClientReferenceId:D}"
                            : $"broker:{order.BrokerOrderId.Trim()}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(order => order.UpdatedAtUtc)
                        .First()),
                ];

                if (exactMatches.Length > 1)
                {
                    const string reason =
                        "Multiple distinct Robinhood orders matched the in-flight intent. PriceSentinel cannot safely determine which order it owns; verify Robinhood immediately.";
                    Publish(reason, "ERROR");
                    RequestDisarm(reason);
                    return null;
                }

                recovered = exactMatches.SingleOrDefault();
            }

            if (recovered is not null)
            {
                return recovered with
                {
                    ClientReferenceId = _activeIntent.ClientReferenceId,
                };
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(1),
                    _timeProvider,
                    cancellationToken);
            }
        }

        return null;
    }

    private void RecordStoppedOrderState(
        Guid? sessionId,
        Instrument instrument,
        BrokerOrderSnapshot order,
        string eventType)
    {
        if (sessionId is null || _activeIntent is null)
        {
            return;
        }

        AppendEvent(
            sessionId.Value,
            instrument,
            eventType,
            _activeIntent,
            _activeReview,
            order);
    }

    private void AppendEvent(
        Guid sessionId,
        Instrument instrument,
        string eventType,
        BrokerOrderIntent intent,
        BrokerOrderReview? review,
        BrokerOrderSnapshot? order) =>
        _journal.AppendLiveOrderEvent(
            sessionId,
            instrument,
            eventType,
            intent,
            review,
            order,
            _timeProvider.GetUtcNow());

    private static void ValidatePlacedOrder(
        BrokerOrderSnapshot order,
        BrokerOrderIntent intent)
    {
        if (string.IsNullOrWhiteSpace(order.BrokerOrderId) ||
            order.State is BrokerOrderState.Unknown ||
            !string.Equals(order.Symbol, intent.Symbol, StringComparison.OrdinalIgnoreCase) ||
            order.Side != intent.Side ||
            order.RequestedQuantity != intent.Quantity)
        {
            throw new InvalidOperationException(
                "Robinhood returned an incomplete or mismatched order acknowledgement. LIVE execution is stopped; verify Robinhood immediately.");
        }
    }

    private void ClearActiveContext()
    {
        _activeOrder = null;
        _activeIntent = null;
        _activeReview = null;
        _activeTriggerTimestamp = null;
    }

    private void Publish(string message, string level = "INFO") =>
        Activity?.Invoke(new(message, level));

    private void RequestDisarm(string reason) => DisarmRequested?.Invoke(reason);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearActiveContext();
        _orderGate.Dispose();
    }
}
