using System.IO;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task OfflineLists_NormalizeDeduplicateSaveAndReloadWithoutProviderCalls() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.NewList();
        vm.ListName = "Streaming and chips";
        vm.TickerInput = " nflx, SOXL\nnflx; NVDA ";
        await vm.AddTickersAsync();
        vm.TickerInput = "soxl NFLX";
        await vm.AddTickersAsync();
        Assert.Equal(new[] { "NFLX", "SOXL", "NVDA" }, vm.Members.Select(item => item.Symbol));
        vm.Members[1].IsIncluded = false;
        await vm.SaveListAsync();

        CollectionState reloaded = new JsonCollectionStateStore(fixture.StatePath).Load();
        DownloadList saved = Assert.Single(reloaded.Settings.Lists);
        Assert.Equal("Streaming and chips", saved.Name);
        Assert.False(saved.Members.Single(item => item.Symbol == "SOXL").IsIncluded);
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.Equal(0, fixture.ConnectionCalls);
        vm.ListName = "Renamed list";
        await vm.SaveListAsync();
        Assert.Equal(saved.Id, Assert.Single(fixture.Collector.State.Settings.Lists).Id);
        Assert.Equal("Renamed list", Assert.Single(vm.Lists).Name);
    });

    [Fact]
    public Task PortableExportImport_PreservesExclusionsWithoutPrivateProviderIds() => host.RunAsync(async () =>
    {
        await using var first = new RetentionFixture();
        first.Provider.Members = [new("instrument", "NFLX", "remote-nflx"), new("instrument", "SOXL", "remote-soxl")];
        await first.ViewModel.LoadWatchlistsAsync();
        await first.ViewModel.PreviewWatchlistAsync(false);
        first.ViewModel.Members[1].IsIncluded = false;
        await first.ViewModel.SaveListAsync();
        string exported = first.ViewModel.ExportLists();
        Assert.DoesNotContain("private-watchlist-id", exported);
        Assert.DoesNotContain("id-NFLX", exported);

        await using var second = new RetentionFixture();
        await second.ViewModel.ImportListsAsync(exported);
        DownloadList imported = Assert.Single(second.ViewModel.Lists);
        Assert.Null(imported.SourceListId);
        Assert.All(imported.Members, member => Assert.Null(member.ProviderInstrumentId));
        Assert.False(imported.Members.Single(member => member.Symbol == "SOXL").IsIncluded);
        Assert.NotEqual(first.ViewModel.Lists[0].Id, imported.Id);
        Assert.Equal(0, second.Provider.Calls);
    });

    [Fact]
    public Task WatchlistRefresh_ReconnectsPreservesExclusionsAndAppliesOnlyAfterSave() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        fixture.Provider.Members = [new("instrument", "NFLX", "id-nflx"), new("instrument", "SOXL", "id-soxl"), new("crypto", null, null)];
        await vm.LoadWatchlistsAsync();
        await vm.PreviewWatchlistAsync(false);
        Assert.Empty(fixture.Collector.State.Settings.Lists);
        vm.Members.Single(item => item.Symbol == "SOXL").IsIncluded = false;
        await vm.SaveListAsync();
        Guid savedId = vm.SelectedList!.Id;

        fixture.Connected = false;
        fixture.Provider.Members = [new("instrument", "SOXL", "id-soxl"), new("instrument", "NVDA", "id-nvda")];
        await vm.PreviewWatchlistAsync(true);
        Assert.True(fixture.Connected);
        Assert.Equal(new[] { "SOXL", "NVDA" }, vm.Members.Select(item => item.Symbol));
        Assert.False(vm.Members.Single(item => item.Symbol == "SOXL").IsIncluded);
        Assert.Contains("1 added, 1 removed", vm.Status);
        Assert.Equal(new[] { "NFLX", "SOXL" }, fixture.Collector.State.Settings.Lists[0].Members.Select(item => item.Symbol));
        await vm.SaveListAsync();
        Assert.Equal(savedId, Assert.Single(vm.Lists).Id);
        Assert.Equal(new[] { "SOXL", "NVDA" }, fixture.Collector.State.Settings.Lists[0].Members.Select(item => item.Symbol));
    });

    [Fact]
    public Task ScheduleDraft_DoesNotChangeClockStateUntilSavedAndPersistsExplicitZone() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.AutomaticDownloadsEnabled = true;
        vm.DailyTime = "10:05";
        vm.TimeZoneId = "America/Los_Angeles";
        Assert.False(vm.SavedAutomaticDownloadsEnabled);
        Assert.Contains("off", vm.SavedSchedule);
        await vm.SaveScheduleAsync();
        Assert.True(vm.SavedAutomaticDownloadsEnabled);
        Assert.Contains("10:05", vm.SavedSchedule);
        Assert.Equal("America/Los_Angeles", new JsonCollectionStateStore(fixture.StatePath).Load().Settings.TimeZoneId);

        vm.AutomaticDownloadsEnabled = false;
        vm.DailyTime = "bad-time";
        await Assert.ThrowsAsync<ArgumentException>(vm.SaveScheduleAsync);
        Assert.True(vm.SavedAutomaticDownloadsEnabled);
        Assert.Equal(new TimeOnly(10, 5), fixture.Collector.State.Settings.DailyDownloadTime);
        Assert.Equal(0, fixture.Provider.Calls);
    });

    [Fact]
    public Task DownloadNow_DiscoversSavedIncludedUnionAndWritesRescannableLibraryWithoutDuplicates() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        vm.TickerInput = "NFLX SOXL";
        await vm.AddTickersAsync();
        vm.Members.Single(member => member.Symbol == "SOXL").IsIncluded = false;
        await vm.SaveListAsync();
        vm.NewList();
        vm.ListName = "Another list";
        vm.TickerInput = "NFLX";
        await vm.AddTickersAsync();
        await vm.SaveListAsync();
        // Unsaved edits must not change the queued dataset's membership.
        vm.TickerInput = "NVDA";
        await vm.AddTickersAsync();
        await vm.DownloadNowAsync();
        CollectionJob job = Assert.Single(fixture.Collector.State.Jobs, job => job.Status == CollectionJobStatus.Complete);
        Assert.Equal("NFLX", job.Symbol);
        Assert.All(fixture.Collector.State.Jobs, item => Assert.Equal("NFLX", item.Symbol));
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        Assert.Equal(1, fixture.Provider.Requests.Count(request => request.FromUtc.Date == new DateTime(2026, 9, 4)));
        await vm.ScanLibraryAsync();
        HistoricalDatasetInfo dataset = Assert.Single(vm.Datasets);
        Assert.True(dataset.Coverage.Complete);
        Assert.Equal(1560, dataset.Coverage.ActualCandleCount);
        Assert.Equal(15, dataset.SourceIntervalSeconds);
        await vm.DownloadNowAsync();
        Assert.Equal(1, fixture.Provider.Requests.Count(request => request.FromUtc.Date == new DateTime(2026, 9, 4)));
        Assert.Single(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets);
    });

    [Fact]
    public Task DownloadNow_CurrentDayStopsAtTheLastCompletedCandleAndLaterCollectsOnlyNewCoverage() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        fixture.Clock.Now = new(2026, 9, 8, 17, 0, 10, TimeSpan.Zero);
        await fixture.SaveSingleSymbol(queueDate: false);
        await fixture.ViewModel.DownloadNowCommand.ExecuteAsync();
        DateTimeOffset firstCutoff = new(2026, 9, 8, 17, 0, 0, TimeSpan.Zero);
        HistoricalDataRequest first = Assert.Single(fixture.Provider.Requests,
            request => request.FromUtc.Date == new DateTime(2026, 9, 8));
        Assert.Equal(firstCutoff, first.ThroughUtc);
        Assert.All(fixture.Provider.Requests, request => Assert.Equal(15, request.SourceIntervalSeconds));
        CollectionJob snapshot = Assert.Single(fixture.Collector.State.Jobs,
            job => job.SessionDate == new DateOnly(2026, 9, 8));
        Assert.Equal(CollectionJobStatus.Complete, snapshot.Status);
        Assert.Equal(firstCutoff, snapshot.RequestedThroughUtc);

        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        await fixture.ViewModel.DownloadNowCommand.ExecuteAsync();
        HistoricalDataRequest[] today = fixture.Provider.Requests
            .Where(request => request.FromUtc.Date == new DateTime(2026, 9, 8)).ToArray();
        Assert.Equal(2, today.Length);
        Assert.Equal(firstCutoff, today[1].FromUtc);
        Assert.Equal(firstCutoff.AddMinutes(1), today[1].ThroughUtc);
    });

    [Fact]
    public Task DownloadNow_RetriesAnOlderUnavailableJobWithoutASeparateRetryAction() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        DateOnly olderDay = new(2026, 8, 10);
        await fixture.Collector.QueueManualAsync(["NFLX"], olderDay, olderDay);
        fixture.Connected = true;
        await fixture.ViewModel.CheckDownloadsAsync();
        CollectionJob missing = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Unavailable, missing.Status);
        Assert.False(missing.IsAvailabilityProbe);

        fixture.Provider.AvailableFrom = olderDay;
        await fixture.ViewModel.DownloadNowCommand.ExecuteAsync();

        CollectionJob recovered = Assert.Single(fixture.Collector.State.Jobs, job => job.Id == missing.Id);
        Assert.Equal(olderDay, recovered.SessionDate);
        Assert.Equal(CollectionJobStatus.Complete, recovered.Status);
        Assert.Equal(2, fixture.Provider.Requests.Count(request =>
            DateOnly.FromDateTime(request.FromUtc.UtcDateTime) == olderDay));
    });

    [Fact]
    public Task CancelInFlightDownload_PreservesPendingJobAndRejectsBusyLibraryRootChange() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        Task downloading = StartQueuedRetentionDownloadsAsync(vm);
        await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        Assert.False(vm.SaveListCommand.CanExecute(null));
        vm.LibraryRootPath = Path.Combine(fixture.Root, "different-library");
        await Assert.ThrowsAsync<InvalidOperationException>(vm.SaveScheduleAsync);
        Assert.Equal(fixture.LibraryRoot, fixture.Collector.State.Settings.LibraryRootPath);
        vm.CancelDownloadsCommand.Execute(null);
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBusy);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(new JsonCollectionStateStore(fixture.StatePath).Load().Jobs).Status);
        Assert.Empty(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets);
        Assert.Contains("cancelled", vm.Status);
    });

    [Fact]
    public Task CancelDuringConnection_DoesNotStartProviderDownloadAndRetainsPendingWork() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.HoldConnection = true;
        Task downloading = StartQueuedRetentionDownloadsAsync(fixture.ViewModel);
        await fixture.ConnectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ViewModel.CancelDownloadsCommand.Execute(null);
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
    });

    [Fact]
    public Task ExplicitReconnect_IsIdleOnlyAndDoesNotQueueDownloads() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        Assert.Equal(0, fixture.ConnectionCalls);
        await fixture.ViewModel.ReconnectCommand.ExecuteAsync();
        Assert.Equal(1, fixture.ConnectionCalls);
        Assert.True(fixture.Connected);
        Assert.Empty(fixture.Collector.State.Jobs);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Contains("connected", fixture.ViewModel.Status);
    });

    [Fact]
    public Task ExplicitReconnect_CanBeCancelledAndDoesNotRunTwice() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        fixture.HoldConnection = true;
        Task reconnect = fixture.ViewModel.ReconnectCommand.ExecuteAsync();
        await fixture.ConnectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.ViewModel.ReconnectCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.DownloadNowCommand.CanExecute(null));
        fixture.ViewModel.CancelDownloadsCommand.Execute(null);
        await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Connected);
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Equal(1, fixture.ConnectionCalls);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
    });

    [Fact]
    public Task OlderContinuityGap_RemainsVisibleAndSplitsWhenOneSessionIsRecovered() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture(new(2026, 8, 24), new(2026, 8, 26));
        Assert.Contains("NFLX: 2026-08-24 through 2026-08-26", fixture.ViewModel.ContinuityWarnings);
        await fixture.SaveSingleSymbol(queueDate: false);
        fixture.Provider.AvailableFrom = new(2026, 8, 25);
        await fixture.Collector.QueueManualAsync(["NFLX"], new(2026, 8, 25), new(2026, 8, 25));
        await StartQueuedRetentionDownloadsAsync(fixture.ViewModel);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Contains("NFLX: 2026-08-24 through 2026-08-24", fixture.ViewModel.ContinuityWarnings);
        Assert.Contains("NFLX: 2026-08-26 through 2026-08-26", fixture.ViewModel.ContinuityWarnings);
        Assert.DoesNotContain("2026-08-25", fixture.ViewModel.ContinuityWarnings);
        Assert.Equal(2, new JsonCollectionStateStore(fixture.StatePath).Load().ContinuityGaps.Count);
    });

    // Keep progress and cancellation fixtures focused on their prequeued jobs. The production
    // Download now discovery path is covered separately; Resume already runs retained work.
    private static async Task StartQueuedRetentionDownloadsAsync(DataRetentionViewModel viewModel)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await viewModel.PauseDownloadsCommand.ExecuteAsync();
        await viewModel.PauseDownloadsCommand.ExecuteAsync();
    }

    private sealed class RetentionFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pricesentinel-retention-tests", Guid.NewGuid().ToString("N"));
        public string LibraryRoot => Path.Combine(Root, "library");
        public string StatePath => Path.Combine(Root, "private-state.json");
        public bool Connected { get; set; }
        public bool HoldConnection { get; set; }
        public int ConnectionCalls { get; private set; }
        public TestClock Clock { get; } = new() { Now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero) };
        public TaskCompletionSource ConnectionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RetentionProvider Provider { get; }
        public MarketDataCollector Collector { get; }
        public DataRetentionViewModel ViewModel { get; }
        public RetentionFixture(DateOnly? gapFrom = null, DateOnly? gapThrough = null, string? timeZoneId = null)
        {
            Provider = new(() => Connected);
            var store = new JsonCollectionStateStore(StatePath);
            store.Save(new CollectionState { Settings = new CollectionSettings
                { LibraryRootPath = LibraryRoot, TimeZoneId = timeZoneId ?? TimeZoneInfo.Local.Id },
                ContinuityGaps = gapFrom is { } from && gapThrough is { } through
                    ? [new("NFLX", from, through, "regular", LibraryRoot)] : [] });
            Collector = new(store, Provider, root => new JsonMarketDataLibrary(root), Clock,
                new CollectionRunOptions { MinimumRequestInterval = TimeSpan.Zero });
            ViewModel = new(Collector, Provider, Provider, Provider, root => new JsonMarketDataLibrary(root), async token =>
            {
                ConnectionCalls++;
                ConnectionStarted.TrySetResult();
                if (HoldConnection) await Task.Delay(Timeout.Infinite, token);
                Connected = true;
            }, () => Connected, clock: Clock);
        }
        public async Task SaveSingleSymbol(bool queueDate = true)
        {
            ViewModel.TickerInput = "NFLX";
            await ViewModel.AddTickersAsync();
            await ViewModel.SaveListAsync();
            if (queueDate) await Collector.QueueManualAsync(["NFLX"], new(2026, 9, 4), new(2026, 9, 4));
        }
        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class RetentionProvider(Func<bool> connected) : IMarketHistoryProvider, IPersonalWatchlistSource, IEquityCatalogSource
    {
        public int Calls { get; private set; }
        public int DownloadCalls { get; private set; }
        public List<HistoricalDataRequest> Requests { get; } = [];
        public DateOnly AvailableFrom { get; set; } = new(2026, 9, 4);
        public bool HoldDownloads { get; set; }
        public IReadOnlyList<PersonalWatchlistMember> Members { get; set; } = [];
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void CheckConnected()
        {
            Calls++;
            if (!connected()) throw new MarketDataConnectionUnavailableException("The provider is disconnected.");
        }
        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            CheckConnected();
            DownloadCalls++;
            Requests.Add(request);
            DownloadStarted.TrySetResult();
            if (HoldDownloads) await Task.Delay(Timeout.Infinite, cancellationToken);
            int count = DateOnly.FromDateTime(request.FromUtc.UtcDateTime) < AvailableFrom ? 0
                : (int)((request.ThroughUtc - request.FromUtc).TotalSeconds / request.SourceIntervalSeconds);
            return new("Robinhood", "id-" + request.Symbol, request.Symbol, request.SourceIntervalSeconds,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, request.ThroughUtc.AddDays(1),
                request.FromUtc, request.ThroughUtc, Enumerable.Range(0, count).Select(index =>
                {
                    DateTimeOffset at = request.FromUtc.AddSeconds(index * request.SourceIntervalSeconds);
                    return new HistoricalCandle(at, at.AddSeconds(request.SourceIntervalSeconds), at.AddSeconds(request.SourceIntervalSeconds),
                        80m, 81m, 79m, 80.5m, 100m);
                }).ToArray());
        }
        public Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken cancellationToken)
        {
            CheckConnected();
            return Task.FromResult<IReadOnlyList<PersonalWatchlist>>([new("private-watchlist-id", "Robinhood list", Members.Count)]);
        }
        public Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(PersonalWatchlist watchlist, CancellationToken cancellationToken)
        {
            CheckConnected();
            return Task.FromResult(new PersonalWatchlistMembers(watchlist, Members));
        }
        public Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken)
        {
            CheckConnected();
            return Task.FromResult<IReadOnlyList<EquityResolution>>(symbols.Select(symbol =>
                new EquityResolution(symbol, symbol, "Company " + symbol, "id-" + symbol, true)).ToArray());
        }
    }
}
