using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class CollectionTimeoutRetryTests
{
    [Theory]
    [InlineData("regular", false)]
    [InlineData("regular", true)]
    [InlineData("24_5", false)]
    [InlineData("24_5", true)]
    public async Task ProviderCancellationRetriesUntilBudgetIsExhaustedWithoutRecordingNoData(string bounds, bool taskCanceled)
    {
        using var fixture = new Fixture(bounds);
        fixture.Provider.Failure = taskCanceled
            ? new TaskCanceledException("Broker request timed out.")
            : new OperationCanceledException("Broker request timed out.");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await fixture.Collector.TickAsync(true);
            Assert.Equal(attempt, fixture.Provider.Requests.Count);
            Assert.Equal(attempt, fixture.Job.Attempts);
            Assert.Equal(attempt < 3 ? CollectionJobStatus.Pending : CollectionJobStatus.Failed, fixture.Job.Status);
            Assert.Empty(fixture.KnownEmptyRanges());
            if (attempt < 3)
            {
                Assert.Equal(fixture.Clock.Now.AddSeconds(5), fixture.Job.RetryAfterUtc);
                Assert.Equal(CollectionBatchResult.WaitingForRetry, await fixture.Collector.TickAsync(true));
                Assert.Equal(attempt, fixture.Provider.Requests.Count);
                fixture.Clock.Now = fixture.Clock.Now.AddSeconds(5);
            }
        }
        Assert.Null(fixture.Job.RetryAfterUtc);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Equal(3, fixture.Provider.Requests.Count);

        fixture.Provider.Failure = null;
        await fixture.Collector.RetryMissingAsync();
        Assert.Equal(0, fixture.Job.Attempts);
        Assert.Equal(CollectionJobStatus.Pending, fixture.Job.Status);
        await fixture.Finish();
        Assert.Equal(CollectionJobStatus.Unavailable, fixture.Job.Status);
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.Equal(bounds == "24_5" ? 1 : 0, fixture.KnownEmptyRanges().Count);
    }

    [Theory]
    [InlineData("regular")]
    [InlineData("24_5")]
    public async Task RecoveredTimeoutSavesDataAndDoesNotBecomeUnavailable(string bounds)
    {
        using var fixture = new Fixture(bounds);
        fixture.Provider.Failure = new TaskCanceledException("Broker request timed out.");
        await fixture.Collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Pending, fixture.Job.Status);
        Assert.Empty(fixture.KnownEmptyRanges());

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(5);
        fixture.Provider.Failure = null;
        fixture.Provider.ReturnCandle = true;
        await fixture.Finish();

        Assert.Equal(CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Library.Scan().Datasets);
        Assert.Empty(fixture.KnownEmptyRanges());
    }

    [Theory]
    [InlineData("regular")]
    [InlineData("24_5")]
    public async Task CallerCancellationRemainsPendingWithoutSpendingRetryBudgetOrRecordingNoData(string bounds)
    {
        using var fixture = new Fixture(bounds);
        using var cancellation = new CancellationTokenSource();
        fixture.Provider.BeforeResponse = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector.TickAsync(true, cancellation.Token));

        Assert.Equal(CollectionJobStatus.Pending, fixture.Job.Status);
        Assert.Equal(0, fixture.Job.Attempts);
        Assert.Null(fixture.Job.RetryAfterUtc);
        Assert.Empty(fixture.KnownEmptyRanges());
        fixture.Provider.BeforeResponse = null;
        fixture.Provider.ReturnCandle = true;
        await fixture.Finish();
        Assert.Equal(CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.Equal(2, fixture.Provider.Requests.Count);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly DateOnly Day = new(2026, 9, 8);
        private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        private readonly string _bounds;
        private readonly DateTimeOffset _from;
        private readonly DateTimeOffset _through;
        private readonly SqliteCollectionGapIndex _index;

        public Fixture(string bounds)
        {
            _bounds = bounds;
            _from = CollectionSchedule.GetSessionWindow(Day, bounds).FromUtc;
            _through = _from.AddSeconds(15);
            Library = new(Path.Combine(_temporaryRoot, "PriceSentinel-timeout-retry-" + Guid.NewGuid().ToString("N")));
            _index = new(Path.Combine(Library.RootPath, ".collection-gaps.sqlite3"));
            var store = new MemoryStore();
            store.Save(new()
            {
                Settings = new() { LibraryRootPath = Library.RootPath },
                Jobs = [new()
                {
                    Symbol = "SOFI", ProviderInstrumentId = "SOFI-id", SessionDate = Day,
                    SessionBounds = bounds, LibraryRootPath = Library.RootPath,
                    RequestedThroughUtc = _through, QueuedAtUtc = Clock.Now,
                }],
            });
            Collector = new(store, Provider, _ => Library, Clock, new()
            {
                MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero,
                MaximumTransientAttempts = 3, RetryDelay = TimeSpan.FromSeconds(5),
            }, gapIndexFactory: _ => _index);
        }

        public Clock Clock { get; } = new();
        public Provider Provider { get; } = new();
        public JsonMarketDataLibrary Library { get; }
        public MarketDataCollector Collector { get; }
        public CollectionJob Job => Assert.Single(Collector.State.Jobs);
        public IReadOnlyList<HistoricalGap> KnownEmptyRanges()
        {
            _index.Initialize();
            return _index.Query(new("SOFI", "SOFI-id", Day, _bounds, "split", "robinhood-split-unversioned"),
                _from, _through, Clock.Now).UnavailableRanges;
        }

        public async Task Finish()
        {
            for (int i = 0; i < 3 && Job.Status == CollectionJobStatus.Pending; i++)
                await Collector.TickAsync(true);
            Assert.NotEqual(CollectionJobStatus.Pending, Job.Status);
        }

        public void Dispose()
        {
            string root = Path.GetFullPath(Library.RootPath);
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("PriceSentinel-timeout-retry-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the test's temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 9, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : ICollectionStateStore
    {
        private string _json = JsonSerializer.Serialize(new CollectionState());
        public CollectionState Load() => JsonSerializer.Deserialize<CollectionState>(_json)!;
        public void Save(CollectionState state) => _json = JsonSerializer.Serialize(state);
    }

    private sealed class Provider : IMarketHistoryProvider
    {
        public List<HistoricalDataRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public Action? BeforeResponse { get; set; }
        public bool ReturnCandle { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            BeforeResponse?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            HistoricalCandle[] candles = ReturnCandle
                ? [new(request.FromUtc, request.ThroughUtc, request.ThroughUtc, 10m, 11m, 9m, 10m, 100)] : [];
            return Task.FromResult(new HistoricalDownload("test", "SOFI-id", request.Symbol, 15,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds,
                request.ThroughUtc, request.FromUtc, request.ThroughUtc, candles));
        }
    }
}
