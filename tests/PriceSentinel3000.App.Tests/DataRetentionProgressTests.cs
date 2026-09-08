using System.Collections.Specialized;
using System.IO;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task DownloadProgress_HeldRequestNamesTickerDateAndExplainsAvailableInteraction() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;

        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Working", vm.DownloadState);
        Assert.True(vm.IsDownloadActive);
        Assert.False(vm.CanEditPlan);
        Assert.Contains("NFLX", vm.DownloadHeading + " " + vm.DownloadDetail);
        Assert.Contains("2026-09-04", vm.DownloadHeading + " " + vm.DownloadDetail);
        Assert.Contains("download", (vm.DownloadHeading + " " + vm.DownloadDetail).ToLowerInvariant());
        Assert.False(string.IsNullOrWhiteSpace(vm.DownloadTiming));
        Assert.Contains("close", vm.DownloadInteractionHint.ToLowerInvariant());
        Assert.True(vm.PauseDownloadsCommand.CanExecute(null));
        Assert.Equal(1, vm.DownloadTotal);
        Assert.Equal(0, vm.DownloadProcessed);
        Assert.Equal(0d, vm.DownloadProgressPercent);

        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Fact]
    public Task DownloadProgress_ConnectingIsVisibleBeforeTheFirstHistoryRequest() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.HoldConnection = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.ConnectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("Working", vm.DownloadState);
        Assert.True(vm.IsDownloadActive);
        Assert.Contains("connect", (vm.DownloadHeading + " " + vm.DownloadDetail).ToLowerInvariant());
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Paused", vm.DownloadState);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
    });

    [Fact]
    public Task DownloadProgress_ExplicitPauseStopsFutureChecksAndResumeFinishesTheSameJob() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Guid id = Assert.Single(vm.Jobs).Id;

        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Paused", vm.DownloadState);
        Assert.False(vm.IsDownloadActive);
        Assert.Contains("resume", vm.PauseDownloadsLabel.ToLowerInvariant());
        Assert.True(vm.CanEditPlan);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(vm.Jobs).Status);
        Assert.Empty(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets);

        fixture.Provider.HoldDownloads = false;
        await vm.CheckDownloadsAsync();
        await vm.CheckDownloadsAsync();
        Assert.Equal(1, fixture.Provider.DownloadCalls);
        Assert.Equal("Paused", vm.DownloadState);

        await vm.PauseDownloadsCommand.ExecuteAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, fixture.Provider.DownloadCalls);
        Assert.Equal(id, Assert.Single(vm.Jobs).Id);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(vm.Jobs).Status);
        Assert.Equal("Complete", vm.DownloadState);
        Assert.Equal(100d, vm.DownloadProgressPercent);
        Assert.False(vm.IsDownloadActive);
        Assert.Single(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets);
    });

    [Fact]
    public Task DownloadProgress_PauseRetainsAllQueuedTickersInsteadOfOnlyTheCurrentRequest() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.TickerInput = "NFLX SOXL MSFT";
        await vm.AddTickersAsync();
        await vm.SaveListAsync();
        await fixture.Collector.QueueManualAsync(["NFLX", "SOXL", "MSFT"], new(2026, 9, 4), new(2026, 9, 4));
        fixture.Provider.HoldDownloads = true;
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal(3, vm.DownloadTotal);
        Assert.Equal(0, vm.DownloadProcessed);
        Assert.All(vm.Jobs, job => Assert.Equal(CollectionJobStatus.Pending, job.Status));
        fixture.Provider.HoldDownloads = false;
        await vm.CheckDownloadsAsync();
        Assert.Equal(1, fixture.Provider.DownloadCalls);
        await vm.PauseDownloadsCommand.ExecuteAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(3, vm.DownloadProcessed);
        Assert.All(vm.Jobs, job => Assert.Equal(CollectionJobStatus.Complete, job.Status));
    });

    [Fact]
    public Task DownloadProgress_ResumedDownloadCanBePausedAgainWhileTheResumeCommandIsStillRunning() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));

        Task resuming = vm.PauseDownloadsCommand.ExecuteAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Provider.DownloadCalls < 2) await Task.Delay(10, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(resuming.IsCompleted);
        Assert.True(vm.PauseDownloadsCommand.CanExecute(null));
        Assert.Contains("pause", vm.PauseDownloadsLabel.ToLowerInvariant());

        await vm.PauseDownloadsCommand.ExecuteAsync();
        await resuming.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Paused", vm.DownloadState);
        Assert.False(vm.IsDownloadActive);
        fixture.Provider.HoldDownloads = false;
        await vm.CheckDownloadsAsync();
        Assert.Equal(2, fixture.Provider.DownloadCalls);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(vm.Jobs).Status);
    });

    [Fact]
    public Task DownloadProgress_UpdatesExistingRowsWithoutResettingTheGridCollection() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Jobs.CollectionChanged += (_, e) => changes.Add(e.Action);
        await fixture.Collector.QueueManualAsync(["NFLX"], new(2026, 9, 4), new(2026, 9, 4));
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        DownloadJobViewModel row = Assert.Single(vm.Jobs);
        var properties = new List<string?>();
        row.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(row, Assert.Single(vm.Jobs));
        Assert.Equal(CollectionJobStatus.Pending, row.Status);
        fixture.Provider.HoldDownloads = false;
        await vm.PauseDownloadsCommand.ExecuteAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Same(row, Assert.Single(vm.Jobs));
        Assert.Equal(CollectionJobStatus.Complete, row.Status);
        Assert.Contains(properties, name => string.IsNullOrEmpty(name) || name == nameof(DownloadJobViewModel.Status));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, changes);
        Assert.Single(changes, change => change == NotifyCollectionChangedAction.Add);
    });

    [Fact]
    public Task DownloadProgress_UnsavedScheduleWarningSurvivesBackgroundUpdatesUntilSave() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.AutomaticDownloadsEnabled = true;
        vm.DailyTime = "14:20";
        Assert.True(vm.HasScheduleChanges);
        Assert.False(vm.SavedAutomaticDownloadsEnabled);
        Assert.Contains("save", vm.ScheduleChangesText.ToLowerInvariant());
        fixture.Provider.HoldDownloads = true;
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(vm.HasScheduleChanges);
        Assert.True(vm.AutomaticDownloadsEnabled);
        Assert.Equal("14:20", vm.DailyTime);
        Assert.False(vm.SavedAutomaticDownloadsEnabled);

        await vm.PauseDownloadsCommand.ExecuteAsync();
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.SaveScheduleAsync();
        Assert.False(vm.HasScheduleChanges);
        Assert.True(vm.SavedAutomaticDownloadsEnabled);
        Assert.Contains("14:20", vm.SavedSchedule);
    });

    [Theory]
    [InlineData("automatic", "UTC")]
    [InlineData("time", "UTC")]
    [InlineData("zone", "UTC")]
    [InlineData("zone", "Pacific Standard Time")]
    [InlineData("bounds", "UTC")]
    [InlineData("folder", "UTC")]
    public Task DownloadProgress_EachScheduleDraftFieldRequiresSave(string field, string savedTimeZone) => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture(timeZoneId: savedTimeZone);
        DataRetentionViewModel vm = fixture.ViewModel;
        Assert.Equal(savedTimeZone, vm.TimeZoneId);
        Assert.False(vm.HasScheduleChanges);
        switch (field)
        {
            case "automatic": vm.AutomaticDownloadsEnabled = !vm.AutomaticDownloadsEnabled; break;
            case "time": vm.DailyTime = "12:34"; break;
            case "zone": vm.TimeZoneId = savedTimeZone == "UTC" ? "Pacific Standard Time" : "UTC"; break;
            case "bounds": vm.SessionBounds = "extended"; break;
            case "folder": vm.LibraryRootPath = Path.Combine(fixture.Root, "other-library"); break;
        }
        Assert.True(vm.HasScheduleChanges);
        await vm.SaveScheduleAsync();
        Assert.False(vm.HasScheduleChanges);
    });

    [Fact]
    public Task DownloadProgress_RevertingScheduleDraftClearsWarningWithoutSaving() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        string savedTime = vm.DailyTime;
        vm.DailyTime = "12:34";
        Assert.True(vm.HasScheduleChanges);
        vm.DailyTime = savedTime;
        Assert.False(vm.HasScheduleChanges);
    });

    [Fact]
    public Task DownloadProgress_EmptyQueueIsIdleInsteadOfClaimingACompletedDownload() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        Assert.Equal("Idle", vm.DownloadState);
        Assert.False(vm.IsDownloadActive);
        Assert.Equal(0, vm.DownloadTotal);
        Assert.Equal(0d, vm.DownloadProgressPercent);
    });

    [Fact]
    public Task DownloadProgress_ContinuesAcrossBatchesAndReportsIntermediateProgress() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(
            new CollectionRunOptions { MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero },
            [new() { Symbol = "NFLX" }, new() { Symbol = "SOXL" }]);
        fixture.Connected = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        var progress = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataRetentionViewModel.DownloadProgressPercent))
                progress.Add(vm.DownloadProgressPercent);
        };
        await vm.CheckDownloadsAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, fixture.Provider.DownloadCalls);
        Assert.Equal(2, vm.DownloadTotal);
        Assert.Equal(2, vm.DownloadProcessed);
        Assert.Contains(50d, progress);
        Assert.Equal(100d, vm.DownloadProgressPercent);
        Assert.Equal("Complete", vm.DownloadState);
        Assert.False(vm.IsDownloadActive);
        Assert.False(vm.HasDownloadWork);
        Assert.False(string.IsNullOrWhiteSpace(vm.DownloadTiming));

        await vm.CheckDownloadsAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, fixture.Provider.DownloadCalls);
        Assert.Equal("Complete", vm.DownloadState);
    });

    [Fact]
    public Task DownloadProgress_ProcessedCountDoesNotDescribeMissingOrFailedDataAsComplete() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(
            new() { Symbol = "NFLX", Status = CollectionJobStatus.Complete },
            new() { Symbol = "SOXL", Status = CollectionJobStatus.Partial },
            new() { Symbol = "MSFT", Status = CollectionJobStatus.Unavailable },
            new() { Symbol = "NVDA", Status = CollectionJobStatus.Failed });
        DataRetentionViewModel vm = fixture.ViewModel;
        Assert.Equal(4, vm.DownloadTotal);
        Assert.Equal(4, vm.DownloadProcessed);
        Assert.Equal(100d, vm.DownloadProgressPercent);
        Assert.Equal("Attention", vm.DownloadState);
        Assert.Contains("1 complete", vm.JobSummary);
        Assert.Contains("3", vm.JobSummary);
        Assert.False(vm.IsDownloadActive);
    });

    [Fact]
    public Task DownloadProgress_EmptyAvailabilityProbeCompletesTheCheckWithoutAnAttentionWarning() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "NFLX", Status = CollectionJobStatus.Unavailable, IsAvailabilityProbe = true,
        });
        DataRetentionViewModel vm = fixture.ViewModel;
        Assert.Equal("Complete", vm.DownloadState);
        Assert.Equal("Available-history check complete", vm.DownloadHeading);
        Assert.Contains("three consecutive", vm.DownloadDetail);
        Assert.Contains("0 need attention", vm.JobSummary);
        Assert.Equal("No data", Assert.Single(vm.Jobs).StateText);
    });

    [Fact]
    public Task DownloadProgress_CurrentDaySnapshotIsSavedSoFarRatherThanACompleteDay() => host.RunAsync(async () =>
    {
        DateTimeOffset cutoff = new(2026, 9, 4, 17, 0, 0, TimeSpan.Zero);
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "NFLX", Status = CollectionJobStatus.Complete, IsAvailabilityProbe = true,
            RequestedThroughUtc = cutoff, ActualSourceIntervalSeconds = 15,
        });
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadJobViewModel row = Assert.Single(vm.Jobs);
        Assert.Equal("Saved so far", row.StateText);
        Assert.Contains(cutoff.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), row.DetailsText);
        Assert.Contains("0 complete", vm.JobSummary);
        Assert.Contains("1 saved so far", vm.JobSummary);
        Assert.Contains("0 need attention", vm.JobSummary);
    });

    [Theory]
    [InlineData(CollectionJobStatus.Partial)]
    [InlineData(CollectionJobStatus.Failed)]
    public Task DownloadProgress_AvailabilityProbeGapsAndErrorsStillNeedAttention(CollectionJobStatus status) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "NFLX", Status = status, IsAvailabilityProbe = true,
        });
        Assert.Equal("Attention", fixture.ViewModel.DownloadState);
        Assert.Contains("1 need attention", fixture.ViewModel.JobSummary);
    });

    [Fact]
    public Task DownloadProgress_DisconnectedQueueWaitsWithoutInteractiveLoginOrPretendingToDownload() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob { Symbol = "NFLX" });
        fixture.Connected = false;
        DataRetentionViewModel vm = fixture.ViewModel;
        await vm.CheckDownloadsAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Waiting", vm.DownloadState);
        Assert.Contains("connect", (vm.DownloadHeading + " " + vm.DownloadDetail).ToLowerInvariant());
        Assert.False(vm.IsDownloadActive);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Equal(0, fixture.ConnectionCalls);
        Assert.Equal(0, vm.DownloadProcessed);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(vm.Jobs).Status);
    });

    [Fact]
    public Task DownloadProgress_ManualConnectionFailureRemainsProminentWithTheQueuePreserved() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.TickerInput = "NFLX";
        await vm.AddTickersAsync();
        await vm.SaveListAsync();
        await vm.Collector.QueueManualAsync(["NFLX"], new(2026, 9, 4), new(2026, 9, 4));
        fixture.ConnectionError = "Robinhood authorization could not be restored.";
        await StartQueuedRetentionDownloadsAsync(vm);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("Attention", vm.DownloadState);
        Assert.Contains(fixture.ConnectionError, vm.DownloadDetail);
        Assert.False(vm.IsDownloadActive);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(vm.Jobs).Status);
        Assert.Equal(0, vm.DownloadProcessed);
    });

    [Fact]
    public Task DownloadProgress_FutureRetryReportsWaitingAndDoesNotRequestDataBeforeItsDeadline() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "NFLX", RetryAfterUtc = new(2026, 9, 7, 20, 10, 0, TimeSpan.Zero),
            Error = "Temporary provider timeout."
        });
        fixture.Connected = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        await vm.CheckDownloadsAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Waiting", vm.DownloadState);
        Assert.Contains("retry", (vm.DownloadHeading + " " + vm.DownloadDetail + " " + vm.DownloadTiming).ToLowerInvariant());
        Assert.False(vm.IsDownloadActive);
        Assert.Equal(0, fixture.Provider.DownloadCalls);

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(11);
        await vm.CheckDownloadsAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, fixture.Provider.DownloadCalls);
        Assert.Equal("Complete", vm.DownloadState);
    });

    [Fact]
    public Task DownloadProgress_HeartbeatRefreshesWaitingCountdownWithoutPollingTheBrokerOrResettingRows() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "NFLX", RetryAfterUtc = new(2026, 9, 7, 20, 10, 0, TimeSpan.Zero),
        });
        fixture.Connected = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.IsBusy) await Task.Delay(10, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Waiting", vm.DownloadState);
        string initialTiming = vm.DownloadTiming;
        DownloadJobViewModel row = Assert.Single(vm.Jobs);
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Jobs.CollectionChanged += (_, e) => changes.Add(e.Action);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataRetentionViewModel.DownloadTiming) && vm.DownloadTiming != initialTiming)
                refreshed.TrySetResult();
        };

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(10);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(initialTiming, vm.DownloadTiming);
        Assert.Equal("Waiting", vm.DownloadState);
        Assert.Same(row, Assert.Single(vm.Jobs));
        Assert.Empty(changes);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Equal(0, fixture.ConnectionCalls);
        Assert.Equal(0, vm.DownloadProcessed);
    });

    [Fact]
    public Task DownloadProgress_DisabledAutomaticJobsAreNotPresentedAsImminentWork() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob { Symbol = "NFLX", IsAutomatic = true });
        fixture.Connected = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        await vm.CheckDownloadsAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(vm.IsDownloadActive);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Contains("automatic", (vm.DownloadHeading + " " + vm.DownloadDetail).ToLowerInvariant());
        Assert.Contains("paused", (vm.DownloadHeading + " " + vm.DownloadDetail).ToLowerInvariant());
        Assert.NotEqual("Complete", vm.DownloadState);
    });

    private sealed class ProgressFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pricesentinel-progress-tests", Guid.NewGuid().ToString("N"));
        public bool Connected { get; set; }
        public string? ConnectionError { get; set; }
        public int ConnectionCalls { get; private set; }
        public TestClock Clock { get; } = new() { Now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero) };
        public RetentionProvider Provider { get; }
        public DataRetentionViewModel ViewModel { get; }

        public ProgressFixture(params CollectionJob[] jobs)
            : this(new CollectionRunOptions { MinimumRequestInterval = TimeSpan.Zero }, jobs) { }

        public ProgressFixture(CollectionRunOptions options, CollectionJob[] jobs)
        {
            string libraryRoot = Path.Combine(_root, "library");
            var store = new JsonCollectionStateStore(Path.Combine(_root, "state.json"));
            store.Save(new CollectionState
            {
                Settings = new CollectionSettings { LibraryRootPath = libraryRoot },
                Jobs = jobs.Select(job => job with
                {
                    LibraryRootPath = libraryRoot, SessionDate = new(2026, 9, 4), QueuedAtUtc = Clock.Now,
                }).ToArray()
            });
            Provider = new(() => Connected);
            var collector = new MarketDataCollector(store, Provider, root => new JsonMarketDataLibrary(root), Clock, options);
            ViewModel = new(collector, Provider, Provider, Provider, root => new JsonMarketDataLibrary(root), _ =>
            {
                ConnectionCalls++;
                if (ConnectionError is not null) throw new InvalidOperationException(ConnectionError);
                Connected = true;
                return Task.CompletedTask;
            }, () => Connected, clock: Clock);
        }

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
