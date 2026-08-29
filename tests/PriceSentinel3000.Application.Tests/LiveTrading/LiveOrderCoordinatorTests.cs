using PriceSentinel3000.Application.LiveTrading;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Application.Tests.LiveTrading;

public sealed class LiveOrderCoordinatorTests
{
    private static readonly Instrument Instrument = new("SOFI");
    private static readonly BrokerAccount Account = new("12345678", true, true, "cash");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public async Task ExecuteAsync_BlocksWhenRobinhoodRejectsReview()
    {
        var gateway = new RecordingGateway
        {
            ReviewAccepted = false,
            ReviewBlockers = ["Account is restricted."],
        };
        var journal = new RecordingJournal();
        using var coordinator = new LiveOrderCoordinator(gateway, journal);
        var activities = new List<LiveOrderActivity>();
        coordinator.Activity += activities.Add;

        LiveOrderOperationResult result = await coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            Intent(),
            CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Null(result.TerminalOrder);
        Assert.Equal(1, gateway.ReviewCalls);
        Assert.Equal(0, gateway.PlaceCalls);
        Assert.False(coordinator.HasActiveContext);
        Assert.Equal(
            ["INTENT_CREATED", "REVIEW_BLOCKED"],
            journal.LiveEvents.Select(item => item.EventType));
        Assert.Contains(activities, item =>
            item.Level == "WARNING" && item.Message.Contains("Account is restricted."));
    }

    [Fact]
    public async Task ExecuteAsync_BlocksWhenReviewedPriceDriftsOverHalfPercent()
    {
        var gateway = new RecordingGateway
        {
            ReviewAskPrice = 10.06m,
        };
        using var coordinator = new LiveOrderCoordinator(
            gateway,
            new RecordingJournal());

        LiveOrderOperationResult result = await coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(ask: 10m),
            Intent(),
            CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal(0, gateway.PlaceCalls);
        Assert.False(coordinator.HasActiveContext);
    }

    [Fact]
    public async Task ExecuteAsync_DisarmsWhenSubmittedOrderEndsRejected()
    {
        BrokerOrderIntent intent = Intent();
        var gateway = new RecordingGateway
        {
            PlaceResult = Order(intent, BrokerOrderState.Rejected) with
            {
                RejectionReason = "Broker risk check failed.",
            },
        };
        var journal = new RecordingJournal();
        using var coordinator = new LiveOrderCoordinator(gateway, journal);
        var disarmReasons = new List<string>();
        coordinator.DisarmRequested += disarmReasons.Add;

        LiveOrderOperationResult result = await coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            intent,
            CancellationToken.None);

        Assert.Equal(BrokerOrderState.Rejected, result.TerminalOrder?.State);
        Assert.Same(intent, result.Intent);
        Assert.Single(disarmReasons);
        Assert.Contains("did not fill successfully", disarmReasons[0]);
        Assert.False(coordinator.HasActiveContext);
        Assert.Equal(
            ["INTENT_CREATED", "REVIEW_ACCEPTED", "SUBMITTED", "TERMINAL"],
            journal.LiveEvents.Select(item => item.EventType));
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedAcknowledgementKeepsContextAndBlocksRetry()
    {
        BrokerOrderIntent intent = Intent();
        var gateway = new RecordingGateway
        {
            PlaceResult = Order(intent, BrokerOrderState.New) with
            {
                Symbol = "MSFT",
            },
        };
        using var coordinator = new LiveOrderCoordinator(
            gateway,
            new RecordingJournal());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(
                Account,
                SessionId,
                Instrument,
                Trigger(),
                intent,
                CancellationToken.None));

        Assert.Contains("mismatched order acknowledgement", exception.Message);
        Assert.True(coordinator.HasActiveContext);
        Assert.Throws<InvalidOperationException>(() => coordinator.Reset());

        LiveOrderOperationResult retry = await coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            Intent(),
            CancellationToken.None);

        Assert.True(retry.Handled);
        Assert.Contains("already active", retry.PendingState);
        Assert.Equal(1, gateway.ReviewCalls);
        Assert.Equal(1, gateway.PlaceCalls);
    }

    [Fact]
    public async Task ExecuteAsync_RecoversOneBrokerOrderFromDuplicateHistoryRows()
    {
        BrokerOrderIntent intent = Intent();
        BrokerOrderSnapshot older = Order(intent, BrokerOrderState.New, "broker-order-1") with
        {
            UpdatedAtUtc = Now.AddSeconds(-1),
        };
        BrokerOrderSnapshot latest = Order(intent, BrokerOrderState.Filled, "broker-order-1") with
        {
            UpdatedAtUtc = Now,
        };
        var gateway = new RecordingGateway
        {
            PlaceHandler = (_, _) => throw new IOException("Lost placement response."),
            RecentOrders = [older, latest],
        };
        using var coordinator = new LiveOrderCoordinator(
            gateway,
            new RecordingJournal(),
            new ImmediateTimeProvider(Now));
        var disarmReasons = new List<string>();
        coordinator.DisarmRequested += disarmReasons.Add;

        LiveOrderOperationResult result = await coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            intent,
            CancellationToken.None);

        Assert.Equal(2, gateway.PlaceCalls);
        Assert.Equal(BrokerOrderState.Filled, result.TerminalOrder?.State);
        Assert.Equal("broker-order-1", result.TerminalOrder?.BrokerOrderId);
        Assert.Empty(disarmReasons);
        Assert.False(coordinator.HasActiveContext);
    }

    [Fact]
    public async Task ExecuteAsync_DisarmsWhenDistinctOrdersMatchUncertainPlacement()
    {
        BrokerOrderIntent intent = Intent();
        var gateway = new RecordingGateway
        {
            PlaceHandler = (_, _) => throw new IOException("Lost placement response."),
            RecentOrders =
            [
                Order(intent, BrokerOrderState.New, "broker-order-1"),
                Order(intent, BrokerOrderState.New, "broker-order-2"),
            ],
        };
        using var coordinator = new LiveOrderCoordinator(
            gateway,
            new RecordingJournal(),
            new ImmediateTimeProvider(Now));
        var disarmReasons = new List<string>();
        coordinator.DisarmRequested += disarmReasons.Add;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            intent,
            CancellationToken.None));

        Assert.Contains(disarmReasons, reason =>
            reason.Contains("Multiple distinct Robinhood orders"));
        Assert.True(coordinator.HasActiveContext);
    }

    [Fact]
    public async Task ReconcileActiveAsync_DisarmsRejectedActiveOrder()
    {
        BrokerOrderIntent intent = Intent();
        using var executionCancellation = new CancellationTokenSource();
        BrokerOrderSnapshot active = Order(intent, BrokerOrderState.New);
        BrokerOrderSnapshot rejected = Order(intent, BrokerOrderState.Rejected) with
        {
            RejectionReason = "Cancelled by broker.",
        };
        var gateway = new RecordingGateway
        {
            PlaceHandler = (_, _) =>
            {
                executionCancellation.Cancel();
                return Task.FromResult(active);
            },
            GetOrderResult = rejected,
        };
        using var coordinator = new LiveOrderCoordinator(gateway, new RecordingJournal());
        var disarmReasons = new List<string>();
        coordinator.DisarmRequested += disarmReasons.Add;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            intent,
            executionCancellation.Token));
        Assert.True(coordinator.HasActiveContext);

        LiveOrderOperationResult result = await coordinator.ReconcileActiveAsync(
            Account,
            SessionId,
            Instrument,
            CancellationToken.None);

        Assert.Equal(BrokerOrderState.Rejected, result.TerminalOrder?.State);
        Assert.Single(disarmReasons);
        Assert.False(coordinator.HasActiveContext);
    }

    [Fact]
    public async Task CancelActiveAsync_CancelsAndReconcilesRetainedOrderContext()
    {
        BrokerOrderIntent intent = Intent();
        using var executionCancellation = new CancellationTokenSource();
        BrokerOrderSnapshot active = Order(intent, BrokerOrderState.New);
        BrokerOrderSnapshot cancelled = Order(intent, BrokerOrderState.Cancelled);
        var gateway = new RecordingGateway
        {
            PlaceHandler = (_, _) =>
            {
                executionCancellation.Cancel();
                return Task.FromResult(active);
            },
            GetOrderResult = cancelled,
        };
        var journal = new RecordingJournal();
        using var coordinator = new LiveOrderCoordinator(
            gateway,
            journal,
            new ImmediateTimeProvider(Now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            intent,
            executionCancellation.Token));
        Assert.True(coordinator.HasActiveContext);

        LiveOrderOperationResult result = await coordinator.CancelActiveAsync(
            Account,
            SessionId,
            Instrument,
            CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Same(intent, result.Intent);
        Assert.Equal(BrokerOrderState.Cancelled, result.TerminalOrder?.State);
        Assert.Equal(1, gateway.CancelCalls);
        Assert.False(coordinator.HasActiveContext);
        Assert.Equal(
            [
                "INTENT_CREATED",
                "REVIEW_ACCEPTED",
                "SUBMITTED",
                "CANCEL_REQUESTED",
                "TERMINAL",
            ],
            journal.LiveEvents.Select(item => item.EventType));
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresConcurrentSignalWhileReviewIsRunning()
    {
        var reviewEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReview = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new RecordingGateway
        {
            ReviewHandler = async (intent, cancellationToken) =>
            {
                reviewEntered.TrySetResult();
                await releaseReview.Task.WaitAsync(cancellationToken);
                return Review(intent, accepted: false, blockers: ["Test block."]);
            },
        };
        using var coordinator = new LiveOrderCoordinator(gateway, new RecordingJournal());
        Task<LiveOrderOperationResult> first = coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            Intent(),
            CancellationToken.None);
        await reviewEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        LiveOrderOperationResult duplicate = await coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            Intent(),
            CancellationToken.None);
        releaseReview.TrySetResult();
        await first;

        Assert.True(duplicate.Handled);
        Assert.Equal(1, gateway.ReviewCalls);
        Assert.Equal(0, gateway.PlaceCalls);
    }

    [Fact]
    public async Task Dispose_ClearsContextAndRejectsFurtherOperations()
    {
        var coordinator = new LiveOrderCoordinator(
            new RecordingGateway(),
            new RecordingJournal());

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.False(coordinator.HasActiveContext);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Reset());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.ExecuteAsync(
            Account,
            SessionId,
            Instrument,
            Trigger(),
            Intent(),
            CancellationToken.None));
    }

    private static BrokerOrderIntent Intent() =>
        new(Guid.NewGuid(), Now, "SOFI", BrokerOrderSide.Buy, 1m, "Test signal.");

    private static MarketQuote Trigger(decimal ask = 10m) =>
        new(Instrument, Now, Now, ask - 0.02m, ask, ask - 0.01m, 1_000m);

    private static BrokerOrderReview Review(
        BrokerOrderIntent intent,
        bool accepted = true,
        IReadOnlyList<string>? blockers = null,
        decimal? askPrice = 10m) =>
        new(
            intent,
            accepted,
            blockers ?? [],
            9.98m,
            askPrice,
            9.99m,
            string.Empty,
            "{}");

    private static BrokerOrderSnapshot Order(
        BrokerOrderIntent intent,
        BrokerOrderState state,
        string brokerOrderId = "broker-order-1") =>
        new(
            intent.ClientReferenceId,
            brokerOrderId,
            intent.Symbol,
            intent.Side,
            state,
            intent.Quantity,
            state is BrokerOrderState.Filled ? intent.Quantity : 0m,
            state is BrokerOrderState.Filled ? 10m : null,
            null,
            Now,
            state is BrokerOrderState.Filled
                ? [new("execution-1", Now, intent.Quantity, 10m)]
                : []);

    private sealed class RecordingGateway : ILiveBrokerGateway
    {
        public bool ReviewAccepted { get; init; } = true;
        public IReadOnlyList<string> ReviewBlockers { get; init; } = [];
        public decimal? ReviewAskPrice { get; init; } = 10m;
        public BrokerOrderSnapshot? PlaceResult { get; init; }
        public BrokerOrderSnapshot? GetOrderResult { get; init; }
        public BrokerOrderSnapshot? FindResult { get; init; }
        public IReadOnlyList<BrokerOrderSnapshot> RecentOrders { get; init; } = [];
        public Func<BrokerOrderIntent, CancellationToken, Task<BrokerOrderReview>>?
            ReviewHandler
        { get; init; }
        public Func<BrokerOrderIntent, CancellationToken, Task<BrokerOrderSnapshot>>?
            PlaceHandler
        { get; init; }
        public int ReviewCalls { get; private set; }
        public int PlaceCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<BrokerOrderReview> ReviewOrderAsync(
            string accountNumber,
            BrokerOrderIntent intent,
            CancellationToken cancellationToken)
        {
            ReviewCalls++;
            return ReviewHandler?.Invoke(intent, cancellationToken) ??
                   Task.FromResult(Review(
                       intent,
                       ReviewAccepted,
                       ReviewBlockers,
                       ReviewAskPrice));
        }

        public Task<BrokerOrderSnapshot> PlaceOrderAsync(
            string accountNumber,
            BrokerOrderIntent intent,
            CancellationToken cancellationToken)
        {
            PlaceCalls++;
            return PlaceHandler?.Invoke(intent, cancellationToken) ??
                   Task.FromResult(PlaceResult ?? Order(intent, BrokerOrderState.Filled));
        }

        public Task<BrokerOrderSnapshot> GetOrderAsync(
            string accountNumber,
            string brokerOrderId,
            CancellationToken cancellationToken) =>
            Task.FromResult(GetOrderResult ?? throw new InvalidOperationException(
                "No broker order response was configured."));

        public Task<BrokerOrderSnapshot?> FindOrderByClientReferenceAsync(
            string accountNumber,
            Instrument instrument,
            Guid clientReferenceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(FindResult);

        public Task<IReadOnlyList<BrokerOrderSnapshot>> GetOrdersCreatedSinceAsync(
            string accountNumber,
            DateTimeOffset createdAtGteUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(RecentOrders);

        public Task<BrokerAccount> GetAgenticAccountAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrokerPortfolio> GetPortfolioAsync(
            string accountNumber,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrokerPosition> GetPositionAsync(
            string accountNumber,
            Instrument instrument,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EquityTradability> GetTradabilityAsync(
            string accountNumber,
            string accountType,
            Instrument instrument,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BrokerOrderSnapshot>> GetOpenOrdersAsync(
            string accountNumber,
            Instrument instrument,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelOrderAsync(
            string accountNumber,
            string brokerOrderId,
            CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedLiveEvent(
        string EventType,
        BrokerOrderIntent Intent,
        BrokerOrderReview? Review,
        BrokerOrderSnapshot? Order);

    private sealed class RecordingJournal : ITradingJournal
    {
        public string DatabasePath => "test";
        public List<RecordedLiveEvent> LiveEvents { get; } = [];

        public void AppendLiveOrderEvent(
            Guid sessionId,
            Instrument instrument,
            string eventType,
            BrokerOrderIntent intent,
            BrokerOrderReview? review,
            BrokerOrderSnapshot? order,
            DateTimeOffset occurredAtUtc) =>
            LiveEvents.Add(new(eventType, intent, review, order));

        public void Initialize()
        {
        }

        public void Dispose()
        {
        }

        public JournalSession StartSession(
            Instrument instrument,
            TradingMode mode,
            decimal startingBalance,
            string settingsJson,
            DateTimeOffset startedAtUtc) => throw new NotSupportedException();

        public void AppendQuotes(
            Guid sessionId,
            IEnumerable<MarketQuote> quotes,
            QuoteIngestionKind ingestionKind) => throw new NotSupportedException();

        public void AppendActivity(
            Guid? sessionId,
            DateTimeOffset occurredAtUtc,
            string level,
            string message) => throw new NotSupportedException();

        public void AppendDecision(Guid sessionId, StrategyDecision decision) =>
            throw new NotSupportedException();

        public void AppendPaperFill(
            Guid sessionId,
            Instrument instrument,
            PaperOrder order,
            PaperFill fill,
            PaperAccountSnapshot account) => throw new NotSupportedException();

        public decimal? GetLiveStartingBalanceSince(DateTimeOffset startedAtGteUtc) =>
            throw new NotSupportedException();

        public void CompleteSession(
            Guid sessionId,
            DateTimeOffset endedAtUtc,
            string outcome) => throw new NotSupportedException();

        public JournalSummary GetSummary(Guid sessionId) => throw new NotSupportedException();

        public ReplaySourceSession? FindLatestReplaySource(Instrument instrument) =>
            throw new NotSupportedException();

        public IReadOnlyList<MarketQuote> ReadSessionQuotes(
            Guid sourceSessionId,
            Instrument instrument) => throw new NotSupportedException();
    }

    private sealed class ImmediateTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ImmediateTimer(callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class ImmediateTimer(
            TimerCallback callback,
            object? state) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (!_disposed)
                        {
                            callback(state);
                        }
                    });
                }

                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
