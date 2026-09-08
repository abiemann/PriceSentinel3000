using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task DownloadPump_OneCommandDrainsDiscoveryAcrossSingleRequestBatches() => host.RunAsync(async () =>
    {
        await using var fixture = new DownloadPumpFixture(clock: new TestClock { Now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero) });
        await fixture.ViewModel.DownloadNowCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.DoesNotContain(fixture.Collector.State.Jobs, j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading);
        Assert.Contains(fixture.Provider.Requests, r => DateOnly.FromDateTime(r.FromUtc.UtcDateTime) < new DateOnly(2026, 9, 1));
        Assert.Contains(fixture.Collector.State.Jobs, j => j.Status == CollectionJobStatus.Complete);
        Assert.True(fixture.Provider.Requests.Count > 2);
        Assert.Equal(1, fixture.Provider.MaximumConcurrentCalls);
        Assert.False(fixture.ViewModel.IsBusy);
    });

    [Fact]
    public Task DownloadPump_DrainsReadyBatchesWhileRespectingProviderRequestSpacing() => host.RunAsync(async () =>
    {
        TimeSpan spacing = TimeSpan.FromMilliseconds(120);
        await using var fixture = new DownloadPumpFixture(options: new()
        {
            MaximumRequestsPerTick = 1, MinimumRequestInterval = spacing,
        });
        await fixture.Queue("NFLX", "SOXL", "MSFT");
        await fixture.ViewModel.CheckDownloadsAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.All(fixture.Collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        Assert.Equal(1, fixture.Provider.MaximumConcurrentCalls);
        for (int index = 1; index < fixture.Provider.RequestTimes.Count; index++)
            Assert.True(Stopwatch.GetElapsedTime(fixture.Provider.RequestTimes[index - 1], fixture.Provider.RequestTimes[index])
                >= spacing - TimeSpan.FromMilliseconds(20), "The next batch bypassed the provider's minimum request spacing.");
    });

    [Fact]
    public Task DownloadPump_PauseDuringSecondBatchCancelsAndResumeFinishesRetainedJobs() => host.RunAsync(async () =>
    {
        await using var fixture = new DownloadPumpFixture();
        await fixture.Queue("NFLX", "SOXL", "MSFT");
        fixture.Provider.HoldCall = 2;
        Task draining = fixture.ViewModel.CheckDownloadsAsync();
        await fixture.Provider.HeldRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ViewModel.PauseDownloadsCommand.ExecuteAsync();
        await draining.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Collector.State.Jobs, j => j.Status == CollectionJobStatus.Complete);
        Assert.Equal(2, fixture.Collector.State.Jobs.Count(j => j.Status == CollectionJobStatus.Pending));
        Assert.Equal("Paused", fixture.ViewModel.DownloadState);
        await fixture.ViewModel.CheckDownloadsAsync();
        Assert.Equal(2, fixture.Provider.Requests.Count);

        fixture.Provider.HoldCall = null;
        await fixture.ViewModel.PauseDownloadsCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.All(fixture.Collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        Assert.Equal(1, fixture.Provider.MaximumConcurrentCalls);
    });

    [Fact]
    public Task DownloadPump_ConnectionExceptionStopsEvenWhenConnectionStatusIsStale() => host.RunAsync(async () =>
    {
        await using var fixture = new DownloadPumpFixture(jobs:
        [
            new() { Symbol = "NFLX" },
            new() { Symbol = "SOXL", RetryAfterUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
        ]);
        fixture.Provider.ConnectionUnavailable = true;
        fixture.ViewModel.Start();
        await fixture.Provider.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForPumpAsync(() => !fixture.ViewModel.IsBusy);
        await Task.Delay(100);

        Assert.True(fixture.Connected);
        Assert.Single(fixture.Provider.Requests);
        Assert.All(fixture.Collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Pending, j.Status));
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(0, fixture.ConnectionCalls);
    });

    [Fact]
    public Task DownloadPump_DisposeAwaitsAHeldResumeAndPreservesPendingWork() => host.RunAsync(async () =>
    {
        await using var fixture = new DownloadPumpFixture();
        await fixture.Queue("NFLX");
        fixture.Provider.HoldCall = 1;
        Task downloading = fixture.ViewModel.CheckDownloadsAsync();
        await fixture.Provider.HeldRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ViewModel.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Provider.HoldCall = 2;
        Task resuming = fixture.ViewModel.PauseDownloadsCommand.ExecuteAsync();
        await WaitForPumpAsync(() => fixture.Provider.Requests.Count == 2 && fixture.Collector.IsBusy);
        Assert.False(resuming.IsCompleted);
        await fixture.ViewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(resuming.IsCompleted);
        await resuming;
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.False(fixture.ViewModel.IsBusy);
        await Task.Delay(50);
        Assert.Equal(2, fixture.Provider.Requests.Count);
    });

    [Theory]
    [InlineData(2, CollectionJobStatus.Complete)]
    [InlineData(5, CollectionJobStatus.Failed)]
    public Task DownloadPump_RetryTimerWaitsForDeadlineThenAutomaticallyResumesWithoutSpinning(
        int failures, CollectionJobStatus finalStatus) => host.RunAsync(async () =>
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(250);
        await using var fixture = new DownloadPumpFixture(options: new()
        {
            MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero,
            RetryDelay = retryDelay, MaximumTransientAttempts = 3,
        });
        await fixture.Queue("NFLX");
        fixture.Provider.FailuresRemaining = failures;
        fixture.ViewModel.Start();
        await fixture.Provider.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForPumpAsync(() => !fixture.ViewModel.IsBusy);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
        await Task.Delay(50);
        Assert.Single(fixture.Provider.Requests);

        await WaitForPumpAsync(() => fixture.Collector.State.Jobs.Single().Status == finalStatus);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        for (int index = 1; index < fixture.Provider.RequestTimes.Count; index++)
            Assert.True(Stopwatch.GetElapsedTime(fixture.Provider.RequestTimes[index - 1], fixture.Provider.RequestTimes[index])
                >= retryDelay - TimeSpan.FromMilliseconds(20), "A retry ran before its retry deadline.");
        await Task.Delay(300);
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Equal(1, fixture.Provider.MaximumConcurrentCalls);
    });

    private static async Task WaitForPumpAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class DownloadPumpFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pricesentinel-pump-tests", Guid.NewGuid().ToString("N"));
        public DownloadPumpFixture(CollectionRunOptions? options = null, TimeProvider? clock = null,
            IReadOnlyList<CollectionJob>? jobs = null)
        {
            string libraryRoot = Path.Combine(_root, "library");
            var store = new JsonCollectionStateStore(Path.Combine(_root, "state.json"));
            clock ??= TimeProvider.System;
            store.Save(new()
            {
                Settings = new()
                {
                    LibraryRootPath = libraryRoot,
                    Lists = [new(Guid.NewGuid(), "Included", true, [new("NFLX"), new("SOXL")])],
                },
                Jobs = jobs?.Select(job => job with
                {
                    LibraryRootPath = libraryRoot, SessionDate = new(2026, 9, 4), QueuedAtUtc = clock.GetUtcNow(),
                }).ToArray() ?? [],
            });
            var innerProvider = new RetentionProvider(() => Connected);
            Provider = new(innerProvider);
            Collector = new(store, Provider, root => new JsonMarketDataLibrary(root), clock, options ?? new()
            {
                MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero,
            });
            ViewModel = new(Collector, Provider, innerProvider, innerProvider, root => new JsonMarketDataLibrary(root), _ =>
            {
                ConnectionCalls++;
                Connected = true;
                return Task.CompletedTask;
            }, () => Connected, clock: clock);
        }
        public bool Connected { get; set; } = true;
        public int ConnectionCalls { get; private set; }
        public DownloadPumpProvider Provider { get; }
        public MarketDataCollector Collector { get; }
        public DataRetentionViewModel ViewModel { get; }
        public Task Queue(params string[] symbols) => Collector.QueueManualAsync(symbols, new(2026, 9, 4), new(2026, 9, 4));
        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class DownloadPumpProvider(RetentionProvider inner) : IMarketHistoryProvider
    {
        private int _activeCalls;
        public List<HistoricalDataRequest> Requests { get; } = [];
        public List<long> RequestTimes { get; } = [];
        public int MaximumConcurrentCalls { get; private set; }
        public int? HoldCall { get; set; }
        public int FailuresRemaining { get; set; }
        public bool ConnectionUnavailable { get; set; }
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeldRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, Interlocked.Increment(ref _activeCalls));
            try
            {
                Requests.Add(request);
                RequestTimes.Add(Stopwatch.GetTimestamp());
                FirstRequest.TrySetResult();
                if (ConnectionUnavailable) throw new MarketDataConnectionUnavailableException("Connection expired before this request.");
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    throw new HttpRequestException("Temporary provider failure.");
                }
                if (HoldCall == Requests.Count)
                {
                    HeldRequest.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return await inner.DownloadHistoryAsync(request, cancellationToken);
            }
            finally { Interlocked.Decrement(ref _activeCalls); }
        }
    }
}
