using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionActivityTests
{
    private static readonly DateOnly Day = new(2026, 9, 4);

    [Fact]
    public async Task HeldProviderShowsCurrentSymbolDateAndPhaseStartThenClearsAfterSaving()
    {
        var fixture = new Fixture();
        var phases = new List<CollectionActivity>();
        bool idleNotified = false;
        fixture.Collector.StateChanged += (_, _) =>
        {
            if (fixture.Collector.Activity is { } activity) phases.Add(activity);
            else if (!fixture.Collector.IsBusy) idleNotified = true;
        };
        await fixture.Collector.QueueManualAsync(["SOFI"], Day, Day);
        idleNotified = false;
        Task tick = fixture.Collector.TickAsync(true);
        await fixture.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(fixture.Collector.IsBusy);
        Assert.Equal(new("Downloading", "SOFI", Day, fixture.Clock.Now), fixture.Collector.Activity);
        Assert.False(tick.IsCompleted);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(3);
        fixture.Provider.Release.SetResult();
        await tick;

        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Equal(new[] { "CheckingSchedule", "CheckingLocalHistory", "Downloading", "Saving" },
            phases.Select(p => p.Stage).Distinct());
        Assert.All(phases.Where(p => p.Stage != "CheckingSchedule"), p =>
        {
            Assert.Equal("SOFI", p.Symbol);
            Assert.Equal(Day, p.SessionDate);
        });
        Assert.Equal(fixture.Clock.Now, phases.First(p => p.Stage == "Saving").SinceUtc);
        Assert.Null(fixture.Collector.Activity);
        Assert.False(fixture.Collector.IsBusy);
        Assert.True(idleNotified);
        Assert.Equal(5, fixture.Store.SaveCount); // Queue, saved coverage, active selection, downloading and complete; activity is transient.
    }

    [Theory]
    [InlineData("invalid", CollectionJobStatus.Failed)]
    [InlineData("transport", CollectionJobStatus.Pending)]
    [InlineData("disconnected", CollectionJobStatus.Pending)]
    public async Task ProviderFailureClearsActivityWithoutChangingRetryBehavior(string failure, CollectionJobStatus expected)
    {
        var fixture = new Fixture();
        fixture.Provider.Failure = failure switch
        {
            "transport" => new HttpRequestException("Temporary transport failure"),
            "disconnected" => new MarketDataConnectionUnavailableException("Connection unavailable"),
            _ => new InvalidDataException("Invalid history"),
        };
        await fixture.Collector.QueueManualAsync(["SOFI"], Day, Day);
        Task tick = fixture.Collector.TickAsync(true);
        await fixture.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Downloading", fixture.Collector.Activity?.Stage);
        fixture.Provider.Release.SetResult();
        await tick;
        Assert.Null(fixture.Collector.Activity);
        Assert.False(fixture.Collector.IsBusy);
        Assert.Equal(expected, Assert.Single(fixture.Collector.State.Jobs).Status);
    }

    [Fact]
    public async Task CancellationClearsActivityAndPreservesPendingWork()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await fixture.Collector.QueueManualAsync(["SOFI"], Day, Day);
        Task tick = fixture.Collector.TickAsync(true, cancellation.Token);
        await fixture.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
        Assert.Null(fixture.Collector.Activity);
        Assert.False(fixture.Collector.IsBusy);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
    }

    [Fact]
    public async Task RateLimitWaitIdentifiesTheNextJobAndCancellationClearsIt()
    {
        var fixture = new Fixture(TimeSpan.FromMinutes(1));
        var waiting = new TaskCompletionSource<CollectionActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Collector.StateChanged += (_, _) =>
        {
            if (fixture.Collector.Activity is { Stage: "WaitingForRateLimit" } activity) waiting.TrySetResult(activity);
        };
        fixture.Provider.Release.SetResult();
        using var cancellation = new CancellationTokenSource();
        await fixture.Collector.QueueManualAsync(["SOFI", "NVDA"], Day, Day);
        Task tick = fixture.Collector.TickAsync(true, cancellation.Token);
        CollectionActivity activity = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new("WaitingForRateLimit", "NVDA", Day, fixture.Clock.Now), activity);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);
        Assert.Equal(1, fixture.Provider.RequestCount);
        Assert.Null(fixture.Collector.Activity);
        Assert.False(fixture.Collector.IsBusy);
        Assert.Equal(CollectionJobStatus.Complete, fixture.Collector.State.Jobs[0].Status);
        Assert.Equal(CollectionJobStatus.Pending, fixture.Collector.State.Jobs[1].Status);
    }

    [Fact]
    public async Task DisconnectedTickReportsScheduleCheckThenReturnsToIdleWithoutRequest()
    {
        var fixture = new Fixture();
        var phases = new List<string>();
        fixture.Collector.StateChanged += (_, _) =>
        {
            if (fixture.Collector.Activity is { } activity) phases.Add(activity.Stage);
        };
        await fixture.Collector.QueueManualAsync(["SOFI"], Day, Day);
        await fixture.Collector.TickAsync(false);
        Assert.Equal(new[] { "CheckingSchedule" }, phases.Distinct());
        Assert.Null(fixture.Collector.Activity);
        Assert.False(fixture.Collector.IsBusy);
        Assert.Equal(0, fixture.Provider.RequestCount);
    }

    private sealed class Fixture
    {
        public MemoryStore Store { get; } = new();
        public HeldProvider Provider { get; } = new();
        public Clock Clock { get; } = new();
        public MarketDataCollector Collector { get; }
        public Fixture(TimeSpan? interval = null) => Collector = new(Store, Provider, _ => new Library(), Clock,
            new() { MinimumRequestInterval = interval ?? TimeSpan.Zero, RetryDelay = TimeSpan.Zero });
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-07T22:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : ICollectionStateStore
    {
        private CollectionState _state = new() { Settings = new() { LibraryRootPath = Path.GetFullPath("test-library") } };
        public int SaveCount { get; private set; }
        public CollectionState Load() => _state;
        public void Save(CollectionState state) { _state = state; SaveCount++; }
    }

    private sealed class HeldProvider : IMarketHistoryProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; set; }
        public int RequestCount { get; private set; }
        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            return new("test", "instrument", request.Symbol, 15, request.AdjustmentPolicy, "robinhood-split-unversioned",
                request.SessionBounds, request.ThroughUtc, request.FromUtc, request.ThroughUtc,
                [new(request.FromUtc, request.FromUtc.AddSeconds(15), request.FromUtc.AddSeconds(15), 10, 11, 9, 10, null)]);
        }
    }

    private sealed class Library : IMarketDataLibrary
    {
        public string RootPath => Path.GetFullPath("test-library");
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => new(true, [], [],
            new(query.FromUtc, query.ThroughUtc, null, null, 1560, 0, false, false, []), []);
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) =>
            [new("hash", "day.json", download.Provider, download.InstrumentId, download.Symbol, Day, 15,
                download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds, download.FetchedAtUtc,
                new(download.RequestedFromUtc, download.RequestedThroughUtc, download.RequestedFromUtc,
                    download.RequestedThroughUtc, 1560, 1560, true, false, []))];
        public MarketDataLibraryScan Scan() => new([], []);
        public HistoricalDataset Read(string datasetHash) => throw new NotSupportedException();
    }
}
