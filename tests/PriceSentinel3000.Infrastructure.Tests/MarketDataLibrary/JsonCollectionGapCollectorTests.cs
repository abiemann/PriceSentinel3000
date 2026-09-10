using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class JsonCollectionGapCollectorTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
    private static readonly CollectionGapKey Key = new("AAPL", "aapl-id", new(2026, 9, 8), "24_5", "split", "robinhood-split-unversioned");

    [Fact]
    public async Task PartialDownloadsRetainMissingCountsAcrossRestartsAndNeverDispatchAThirdNormalAttempt()
    {
        using var fixture = new Fixture();
        fixture.Provider.NextCandles = [Bar(0)];
        fixture.Start();
        await fixture.Drain();
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(new(new(From.AddSeconds(15), From.AddMinutes(1)), 1), Assert.Single(fixture.Snapshot().AttemptedRanges));

        fixture.Provider.NextCandles = [Bar(2)];
        fixture.Start();
        await fixture.Drain();
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(new[] { new CollectionGapAttempt(new(From.AddSeconds(15), From.AddSeconds(30)), 2),
            new(new(From.AddSeconds(45), From.AddMinutes(1)), 2) }, fixture.Snapshot().AttemptedRanges);
        Assert.Equal(fixture.Snapshot().AttemptedRanges.Select(item => item.Gap), fixture.Snapshot().UnavailableRanges);

        fixture.Provider.NextCandles = [Bar(1), Bar(3)];
        fixture.Start();
        await fixture.Drain();

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(CollectionJobStatus.Partial, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Equal(new[] { Bar(0), Bar(2) }, fixture.Read().Candles);
    }

    [Fact]
    public async Task ForcedDownloadBypassesThePersistentCapAndClearsOnlyThePartItSaves()
    {
        using var fixture = new Fixture();
        fixture.Provider.NextCandles = [Bar(0)];
        fixture.Start();
        await fixture.Drain();
        fixture.Provider.NextCandles = [Bar(2)];
        fixture.Start();
        await fixture.Drain();
        Assert.Equal(2, fixture.Provider.Requests.Count);

        fixture.Provider.NextCandles = [Bar(1)];
        fixture.Start(force: true);
        await fixture.Drain();

        Assert.Equal(3, fixture.Provider.Requests.Count);
        HistoricalGap remaining = new(From.AddSeconds(45), From.AddMinutes(1));
        Assert.Equal(new(remaining, 2), Assert.Single(fixture.Snapshot().AttemptedRanges));
        Assert.Equal(remaining, Assert.Single(fixture.Snapshot().UnavailableRanges));
        Assert.Equal(new[] { Bar(0), Bar(1), Bar(2) }, fixture.Read().Candles);
        fixture.Start();
        await fixture.Drain();
        Assert.Equal(3, fixture.Provider.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CappedFailedOrInterruptedRetryKeepsItsFailureAndDoesNotClaimUnavailable(bool interrupted)
    {
        using var fixture = new Fixture();
        fixture.Start();
        await fixture.Drain();
        Assert.Single(fixture.Provider.Requests);
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.BeforeResponse = () =>
        {
            if (interrupted)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw new HttpRequestException("The second request failed.");
        };
        fixture.Start(maximumTransientAttempts: 2);
        if (interrupted)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector.TickAsync(true, cancellation.Token));
        else
            Assert.Equal(CollectionBatchResult.Ready, await fixture.Collector.TickAsync(true));
        CollectionJob pending = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Pending, pending.Status);
        Assert.NotNull(pending.Error);

        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));

        CollectionJob failed = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Failed, failed.Status);
        Assert.Equal(pending.Error, failed.Error);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(2, Assert.Single(fixture.Snapshot().AttemptedRanges).Attempts);
    }

    private static HistoricalCandle Bar(int index)
    {
        DateTimeOffset at = From.AddSeconds(index * 15);
        return new(at, at.AddSeconds(15), at.AddSeconds(15), 80, 81, 79, 80, 100);
    }

    private sealed class Fixture : IDisposable
    {
        public JsonMarketDataLibrary Library { get; } = new(Path.Combine(Path.GetTempPath(), "PriceSentinel-json-gap-collector-" + Guid.NewGuid().ToString("N")));
        public MemoryStore Store { get; } = new();
        public Clock Clock { get; } = new();
        public Provider Provider { get; } = new();
        public MarketDataCollector Collector { get; private set; } = null!;

        public void Start(bool force = false, int maximumTransientAttempts = 1)
        {
            Store.Save(new() { Settings = new() { LibraryRootPath = Library.RootPath }, Jobs = [new()
            {
                Symbol = Key.Symbol, ProviderInstrumentId = Key.InstrumentId, SessionDate = Key.SessionDate,
                SessionBounds = Key.SessionBounds, LibraryRootPath = Library.RootPath, QueuedAtUtc = Clock.GetUtcNow(),
                RequestedFromUtc = From, RequestedThroughUtc = From.AddMinutes(1), IgnoreKnownGaps = force,
            }] });
            Collector = new(Store, Provider, _ => new JsonMarketDataLibrary(Library.RootPath), Clock,
                new() { MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero, MaximumTransientAttempts = maximumTransientAttempts, RetryDelay = TimeSpan.Zero },
                root => new JsonCollectionGapIndex(root));
        }

        public CollectionGapSnapshot Snapshot() => new JsonCollectionGapIndex(Library.RootPath).Query(Key, From, From.AddMinutes(1), Clock.GetUtcNow());
        public HistoricalDataQueryResult Read() => Library.Query(new(Key.Symbol, From, From.AddMinutes(1)));

        public async Task Drain()
        {
            for (int tick = 0; tick < 10; tick++)
            {
                if (await Collector.TickAsync(true) == CollectionBatchResult.Idle)
                {
                    Assert.DoesNotContain(Collector.State.Jobs, job => job.Status == CollectionJobStatus.Failed);
                    return;
                }
            }
            throw new InvalidOperationException("Collector did not finish the one-minute test range.");
        }

        public void Dispose()
        {
            if (Directory.Exists(Library.RootPath)) Directory.Delete(Library.RootPath, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 9, 14, 0, 0, TimeSpan.Zero);
    }
    private sealed class MemoryStore : ICollectionStateStore
    {
        private string _state = JsonSerializer.Serialize(new CollectionState());
        public CollectionState Load() => JsonSerializer.Deserialize<CollectionState>(_state)!;
        public void Save(CollectionState state) => _state = JsonSerializer.Serialize(state);
    }
    private sealed class Provider : IMarketHistoryProvider
    {
        public List<HistoricalDataRequest> Requests { get; } = [];
        public HistoricalCandle[] NextCandles { get; set; } = [];
        public Action? BeforeResponse { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            BeforeResponse?.Invoke();
            return Task.FromResult(new HistoricalDownload("Robinhood", "aapl-id", request.Symbol, 15, "split",
                "robinhood-split-unversioned", request.SessionBounds, new Clock().GetUtcNow(), request.FromUtc, request.ThroughUtc,
                NextCandles.Where(item => item.StartsAtUtc >= request.FromUtc && item.EndsAtUtc <= request.ThroughUtc).ToArray()));
        }
    }
}
