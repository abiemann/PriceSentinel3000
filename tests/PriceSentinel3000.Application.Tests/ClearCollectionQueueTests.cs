using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class ClearCollectionQueueTests
{
    private static readonly DateOnly Day = new(2026, 9, 4);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T20:00:00Z");

    [Fact]
    public async Task ClearFinishedJobsPersistsAcrossRestartAndPreservesSettingsAndContinuity()
    {
        var store = new MemoryStore(new()
        {
            Settings = new()
            {
                LibraryRootPath = Path.GetFullPath("test-library"), TimeZoneId = "UTC",
                AutomaticDownloadsEnabled = true, AutomaticEnabledAtUtc = Now.AddDays(-2),
                DailyDownloadTime = new(16, 15), SessionBounds = "24_5",
                Lists = [new(Guid.NewGuid(), "Saved equities", true, [new("AAPL", "Apple")])],
            },
            LastScheduledOccurrenceUtc = Now.AddDays(-1),
            AvailabilityRun = new() { AsOfDate = Day, CurrentDate = Day, Members = [new("AAPL")] },
            ContinuityGaps = [new("AAPL", Day.AddDays(-1), Day, "24_5", Path.GetFullPath("test-library"))],
            Jobs = [Job(CollectionJobStatus.Complete), Job(CollectionJobStatus.Partial),
                Job(CollectionJobStatus.Unavailable), Job(CollectionJobStatus.Failed)],
        });
        MarketDataCollector collector = Create(store);
        CollectionState before = collector.State;
        int notifications = 0;
        collector.StateChanged += (_, _) => notifications++;

        await collector.ClearFinishedJobsAsync();

        Assert.Empty(collector.State.Jobs);
        Assert.Empty(store.Load().Jobs);
        MarketDataCollector restarted = Create(store);
        Assert.Empty(restarted.State.Jobs);
        Assert.Null(restarted.State.AvailabilityRun);
        Assert.Equal(JsonSerializer.Serialize(before.Settings), JsonSerializer.Serialize(restarted.State.Settings));
        Assert.Equal(before.LastScheduledOccurrenceUtc, restarted.State.LastScheduledOccurrenceUtc);
        Assert.Equal(before.ContinuityGaps, restarted.State.ContinuityGaps);
        Assert.Equal(before.SchemaVersion, restarted.State.SchemaVersion);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(CollectionJobStatus.Pending, false, false)]
    [InlineData(CollectionJobStatus.Pending, true, false)]
    [InlineData(CollectionJobStatus.Pending, false, true)]
    [InlineData(CollectionJobStatus.Pending, true, true)]
    [InlineData(CollectionJobStatus.Downloading, false, false)]
    public async Task QueuedWorkCannotBeClearedEvenWhenAutomaticDownloadsAreOffOrRetryIsDelayed(
        CollectionJobStatus status, bool automatic, bool delayed)
    {
        var store = new MemoryStore(new()
        {
            Jobs = [Job(CollectionJobStatus.Complete), Job(status) with
            {
                IsAutomatic = automatic, RetryAfterUtc = delayed ? Now.AddHours(1) : null,
            }],
        });
        MarketDataCollector collector = Create(store);
        string before = JsonSerializer.Serialize(collector.State);
        int saves = store.SaveCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.ClearFinishedJobsAsync());

        Assert.Equal(before, JsonSerializer.Serialize(collector.State));
        Assert.Equal(before, JsonSerializer.Serialize(Create(store).State));
        Assert.Equal(saves, store.SaveCount);
        Assert.False(collector.IsBusy);
    }

    [Fact]
    public async Task ActiveRequestRejectsClearImmediatelyAndPreservesTheDownload()
    {
        var store = new MemoryStore(new() { Jobs = [Job(CollectionJobStatus.Pending)] });
        var provider = new HeldProvider();
        var collector = new MarketDataCollector(store, provider, _ => new EmptyLibrary(), new Clock(),
            new() { MinimumRequestInterval = TimeSpan.Zero });
        using var cancellation = new CancellationTokenSource();
        Task tick = collector.TickAsync(true, cancellation.Token);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(collector.IsBusy);
            int saves = store.SaveCount;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => collector.ClearFinishedJobsAsync()).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(CollectionJobStatus.Downloading, Assert.Single(collector.State.Jobs).Status);
            Assert.Equal(saves, store.SaveCount);
            Assert.False(tick.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
        }
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(collector.State.Jobs).Status);
    }

    [Fact]
    public async Task FailedPersistenceLeavesFinishedHistoryIntact()
    {
        var store = new MemoryStore(new() { Jobs = [Job(CollectionJobStatus.Complete)] });
        MarketDataCollector collector = Create(store);
        store.RejectSave = true;

        await Assert.ThrowsAsync<IOException>(() => collector.ClearFinishedJobsAsync());

        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(collector.State.Jobs).Status);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(Create(store).State.Jobs).Status);
    }

    private static CollectionJob Job(CollectionJobStatus status) => new()
    {
        Symbol = "AAPL", SessionDate = Day, Status = status,
        LibraryRootPath = Path.GetFullPath("test-library"), QueuedAtUtc = Now.AddDays(-1),
        DatasetHashes = status is CollectionJobStatus.Complete or CollectionJobStatus.Partial ? ["saved-hash"] : [],
    };

    private static MarketDataCollector Create(MemoryStore store) => new(store, new HeldProvider(),
        _ => throw new InvalidOperationException("Clearing history must not access candle files."), new Clock(),
        gapIndexFactory: _ => throw new InvalidOperationException("Clearing history must not access the gap index."));

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore(CollectionState initial) : ICollectionStateStore
    {
        private string _json = JsonSerializer.Serialize(initial);
        public int SaveCount { get; private set; }
        public bool RejectSave { get; set; }
        public CollectionState Load() => JsonSerializer.Deserialize<CollectionState>(_json)!;
        public void Save(CollectionState state)
        {
            if (RejectSave) throw new IOException("State file is unavailable.");
            _json = JsonSerializer.Serialize(state);
            SaveCount++;
        }
    }

    private sealed class HeldProvider : IMarketHistoryProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The test must cancel the held request.");
        }
    }

    private sealed class EmptyLibrary : IMarketDataLibrary
    {
        public string RootPath => Path.GetFullPath("test-library");
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => new(true, [], [],
            new(query.FromUtc, query.ThroughUtc, null, null, 1560, 0, false, false, []), []);
        public MarketDataLibraryScan Scan() => new([], []);
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => throw new NotSupportedException();
        public HistoricalDataset Read(string datasetHash) => throw new NotSupportedException();
    }
}
