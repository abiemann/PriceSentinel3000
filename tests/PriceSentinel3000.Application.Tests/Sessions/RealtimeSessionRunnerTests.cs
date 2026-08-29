using PriceSentinel3000.Application.Sessions;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.Tests.Sessions;

public sealed class RealtimeSessionRunnerTests
{
    private static readonly Instrument Instrument = new("SOFI");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_LoadsWarmStartBeforeYieldingCurrentQuote()
    {
        MarketQuote warmQuote = Quote(Now.AddMinutes(-1), 9m);
        MarketQuote currentQuote = Quote(Now, 10m);
        var source = new RecordingMarketDataSource([warmQuote], currentQuote);
        var request = new MarketDataRequest(
            Instrument,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(15));
        var runner = new RealtimeSessionRunner(source, new FixedTimeProvider(Now));
        await using IAsyncEnumerator<RealtimeSessionUpdate> enumerator = runner
            .RunAsync(request, 45, 300, 30, CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());

        RealtimeSessionUpdate update = enumerator.Current;
        Assert.Equal([warmQuote], update.WarmStart);
        Assert.Equal(currentQuote, update.Quote);
        Assert.Empty(update.Reconciliation);
        HistoryCall history = Assert.Single(source.HistoryCalls);
        Assert.Equal(Now.AddMinutes(-15), history.FromUtc);
        Assert.Equal(Now, history.ThroughUtc);
        Assert.Equal(Now, history.ObservedAtUtc);
        Assert.Equal(Now, Assert.Single(source.QuoteObservations));
    }

    [Fact]
    public async Task RunAsync_ReconcilesTheConfiguredCompletedLookback()
    {
        MarketQuote initialQuote = Quote(Now, 10m);
        MarketQuote reconciliationQuote = Quote(Now.AddSeconds(-45), 9.9m);
        var source = new RecordingMarketDataSource([initialQuote], initialQuote)
        {
            Reconciliation = [reconciliationQuote],
        };
        var request = new MarketDataRequest(
            Instrument,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMinutes(1));
        var runner = new RealtimeSessionRunner(source, new FixedTimeProvider(Now));
        await using IAsyncEnumerator<RealtimeSessionUpdate> enumerator = runner
            .RunAsync(request, 0, 300, 30, CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());

        RealtimeSessionUpdate update = enumerator.Current;
        Assert.Empty(update.WarmStart);
        Assert.Equal([reconciliationQuote], update.Reconciliation);
        Assert.Equal(2, source.HistoryCalls.Count);
        HistoryCall reconciliation = source.HistoryCalls[1];
        DateTimeOffset observedAt = source.QuoteObservations[1];
        Assert.Equal(observedAt.AddSeconds(-30), reconciliation.ThroughUtc);
        Assert.Equal(observedAt.AddSeconds(-330), reconciliation.FromUtc);
        Assert.Equal(observedAt, reconciliation.ObservedAtUtc);
    }

    private static MarketQuote Quote(DateTimeOffset timestamp, decimal last) =>
        new(
            Instrument,
            timestamp,
            timestamp,
            last - 0.01m,
            last + 0.01m,
            last,
            1_000m);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
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

    private sealed record HistoryCall(
        DateTimeOffset FromUtc,
        DateTimeOffset ThroughUtc,
        DateTimeOffset ObservedAtUtc);

    private sealed class RecordingMarketDataSource(
        IReadOnlyList<MarketQuote> warmStart,
        MarketQuote quote) : IMarketDataSource
    {
        public string Name => "Recording market data";
        public IReadOnlyList<MarketQuote> Reconciliation { get; init; } = [];
        public List<HistoryCall> HistoryCalls { get; } = [];
        public List<DateTimeOffset> QuoteObservations { get; } = [];

        public Task ConnectAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<MarketQuote>> GetHistoryAsync(
            MarketDataRequest request,
            DateTimeOffset fromUtc,
            DateTimeOffset throughUtc,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
        {
            HistoryCalls.Add(new(fromUtc, throughUtc, observedAtUtc));
            IReadOnlyList<MarketQuote> result = HistoryCalls.Count == 1
                ? warmStart
                : Reconciliation;
            return Task.FromResult(result);
        }

        public Task<MarketQuote> GetQuoteAsync(
            MarketDataRequest request,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
        {
            QuoteObservations.Add(observedAtUtc);
            return Task.FromResult(quote with
            {
                ObservedAtUtc = observedAtUtc,
                SourceTimestampUtc = observedAtUtc,
            });
        }

        public Task<IReadOnlyList<MarketQuote>> GetReplayHistoryAsync(
            Instrument instrument,
            DateTimeOffset fromUtc,
            DateTimeOffset throughUtc,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
