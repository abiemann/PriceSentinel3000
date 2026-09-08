using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class MarketDataCollectorTests
{
    private static readonly DateOnly Day = new(2026, 9, 4);

    [Fact]
    public async Task DisconnectedDueQueueIsDeduplicatedAndSurvivesMembershipChangesAndRestart()
    {
        var (collector, store, provider, library, clock) = Create();
        clock.Now = DateTimeOffset.Parse("2026-09-04T19:00:00Z");
        await collector.SaveSettingsAsync(Settings() with { AutomaticDownloadsEnabled = true });
        clock.Now = DateTimeOffset.Parse("2026-09-04T20:15:00Z");
        await collector.TickAsync(false);
        Assert.Equal(new[] { "SOFI", "NVDA" }, collector.State.Jobs.Select(j => j.Symbol).Distinct());
        Assert.Empty(provider.Requests);
        await collector.SaveSettingsAsync(collector.State.Settings with { Lists = [] });
        var restarted = new MarketDataCollector(store, provider, _ => library, clock, Options());
        await restarted.TickAsync(true);
        await restarted.TickAsync(true);
        Assert.Equal(collector.State.Jobs.Count, provider.Requests.Count);
        Assert.All(restarted.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        await restarted.TickAsync(true);
        Assert.Equal(collector.State.Jobs.Count, provider.Requests.Count);
    }

    [Fact]
    public async Task AutomaticOffPausesQueuedWorkAndManualRequestsStillRun()
    {
        var (collector, _, provider, _, clock) = Create();
        clock.Now = DateTimeOffset.Parse("2026-09-04T19:00:00Z");
        await collector.SaveSettingsAsync(Settings() with { AutomaticDownloadsEnabled = true });
        clock.Now = DateTimeOffset.Parse("2026-09-04T20:15:00Z");
        await collector.TickAsync(false);
        await collector.SaveSettingsAsync(collector.State.Settings with { AutomaticDownloadsEnabled = false });
        await collector.TickAsync(true);
        Assert.Empty(provider.Requests);
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        await collector.TickAsync(true);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ExistingCompleteFineDatasetSkipsProviderAndRootRemainsPinned()
    {
        var (collector, _, provider, library, _) = Create();
        library.HasCompleteData = true;
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        string pinned = Assert.Single(collector.State.Jobs).LibraryRootPath;
        await collector.SaveSettingsAsync(collector.State.Settings with { LibraryRootPath = Path.GetFullPath("another-library") });
        await collector.TickAsync(true);
        Assert.Empty(provider.Requests);
        Assert.Equal(pinned, Assert.Single(collector.State.Jobs).LibraryRootPath);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(collector.State.Jobs).Status);
    }

    [Fact]
    public async Task EmptyFineHistoryNeverFallsBackToCoarseEvenAcrossTicks()
    {
        var (collector, _, provider, _, _) = Create(Options() with { MaximumRequestsPerTick = 1 });
        provider.EmptyIntervals.Add(15);
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Unavailable, Assert.Single(collector.State.Jobs).Status);
        await collector.TickAsync(true);
        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Unavailable, job.Status);
        Assert.Null(job.ActualSourceIntervalSeconds);
        Assert.Equal(new[] { 15 }, provider.Requests.Select(r => r.SourceIntervalSeconds));
    }

    [Fact]
    public async Task EmptyProviderIsUnavailableAndManualRetryIsExplicit()
    {
        var (collector, _, provider, _, _) = Create();
        provider.EmptyIntervals.UnionWith([15, 30, 60]);
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Unavailable, Assert.Single(collector.State.Jobs).Status);
        await collector.TickAsync(true);
        Assert.Single(provider.Requests);
        provider.EmptyIntervals.Clear();
        await collector.RetryMissingAsync();
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(collector.State.Jobs).Status);
    }

    [Fact]
    public async Task TransientFailuresAreBoundedAndDoNotBlockOtherSymbols()
    {
        var (collector, _, provider, _, _) = Create();
        provider.FailingSymbol = "SOFI";
        await collector.QueueManualAsync(["SOFI", "NVDA"], Day, Day);
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Pending, collector.State.Jobs[0].Status);
        Assert.Equal(CollectionJobStatus.Complete, collector.State.Jobs[1].Status);
        await collector.TickAsync(true);
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Failed, collector.State.Jobs[0].Status);
        Assert.Equal(3, collector.State.Jobs[0].Attempts);
    }

    [Fact]
    public async Task CancellationPersistsPendingAndRestartRecoversDownloading()
    {
        var (collector, store, provider, library, clock) = Create();
        using var cancellation = new CancellationTokenSource();
        provider.OnRequest = () => cancellation.Cancel();
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.TickAsync(true, cancellation.Token));
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(store.Load().Jobs).Status);
        store.Save(store.Load() with { Jobs = [store.Load().Jobs[0] with { Status = CollectionJobStatus.Downloading }] });
        provider.OnRequest = null;
        var restarted = new MarketDataCollector(store, provider, _ => library, clock, Options());
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(restarted.State.Jobs).Status);
        await restarted.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(restarted.State.Jobs).Status);
    }

    [Fact]
    public async Task InvalidOrFutureRangeDoesNotQueueAnyWork()
    {
        var (collector, _, _, _, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => collector.QueueManualAsync(["SOFI"], Day, new(2026, 9, 8)));
        Assert.Empty(collector.State.Jobs);
    }

    [Fact]
    public async Task DateRangeMayEndOnCompletedWeekendOrHoliday()
    {
        var (collector, _, _, _, _) = Create();
        await collector.QueueManualAsync(["SOFI"], Day, new(2026, 9, 7));
        Assert.Equal(Day, Assert.Single(collector.State.Jobs).SessionDate);
    }

    [Fact]
    public async Task MidBatchDisconnectPersistsPendingAndStopsFurtherRequestsUntilNextConnectedTick()
    {
        var (collector, _, provider, _, _) = Create();
        provider.Disconnected = true;
        await collector.QueueManualAsync(["SOFI", "NVDA"], Day, Day);
        await collector.TickAsync(true);
        Assert.Single(provider.Requests);
        Assert.All(collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Pending, j.Status));
        Assert.Equal(0, collector.State.Jobs[0].Attempts);
        await collector.TickAsync(false);
        Assert.Single(provider.Requests);
        provider.Disconnected = false;
        await collector.TickAsync(true);
        Assert.All(collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
    }

    [Fact]
    public async Task CoverageGapsRemainPartialWithoutTryingCoarserBars()
    {
        var (collector, _, provider, library, _) = Create();
        library.SaveHasGaps = true;
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        await collector.TickAsync(true);
        Assert.Equal(15, Assert.Single(provider.Requests).SourceIntervalSeconds);
        Assert.Equal(CollectionJobStatus.Partial, Assert.Single(collector.State.Jobs).Status);
    }

    [Fact]
    public void PortableListExportRemovesAllPrivateIdentifiersAndNames()
    {
        var list = new DownloadList(Guid.NewGuid(), "Selected", true,
            [new("SOFI", "Company", false, "private-instrument")], "private-list", "private-source-name");
        string json = DownloadListTransfer.Export([list]);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("Company", json);
        DownloadList imported = Assert.Single(DownloadListTransfer.Import(json));
        Assert.Null(imported.SourceListId);
        Assert.Null(Assert.Single(imported.Members).ProviderInstrumentId);
        Assert.False(imported.Members[0].IsIncluded);
        Assert.NotEqual(list.Id, imported.Id);
    }

    private static CollectionSettings Settings() => new()
    {
        LibraryRootPath = Path.GetFullPath("test-library"), TimeZoneId = "America/Los_Angeles",
        Lists = [new(Guid.NewGuid(), "First", true, [new("SOFI"), new("NVDA"), new("TSLA", IsIncluded: false)]),
            new(Guid.NewGuid(), "Second", true, [new("SOFI")]), new(Guid.NewGuid(), "Excluded", false, [new("MSFT")])],
    };
    private static CollectionRunOptions Options() => new() { MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero };
    private static (MarketDataCollector, MemoryStore, Provider, Library, Clock) Create(CollectionRunOptions? options = null)
    {
        var store = new MemoryStore();
        store.Save(new() { Settings = Settings() });
        var provider = new Provider();
        var library = new Library();
        var clock = new Clock();
        return (new(store, provider, _ => library, clock, options ?? Options()), store, provider, library, clock);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-07T22:00:00Z");
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
        public HashSet<int> EmptyIntervals { get; } = [];
        public string? FailingSymbol { get; set; }
        public bool Disconnected { get; set; }
        public Action? OnRequest { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnRequest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Disconnected) throw new MarketDataConnectionUnavailableException("Connection unavailable");
            if (request.Symbol == FailingSymbol) throw new HttpRequestException("Temporary transport failure");
            HistoricalCandle[] bars = EmptyIntervals.Contains(request.SourceIntervalSeconds) ? [] :
                [new(request.FromUtc, request.FromUtc.AddSeconds(request.SourceIntervalSeconds), request.FromUtc.AddSeconds(request.SourceIntervalSeconds), 10, 11, 9, 10, null)];
            return Task.FromResult(new HistoricalDownload("test", "instrument", request.Symbol, request.SourceIntervalSeconds,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, request.ThroughUtc,
                request.FromUtc, request.ThroughUtc, bars));
        }
    }
    private sealed class Library : IMarketDataLibrary
    {
        public string RootPath => Path.GetFullPath("test-library");
        public bool HasCompleteData { get; set; }
        public bool SaveHasGaps { get; set; }
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => new(true, [], [],
            new(query.FromUtc, query.ThroughUtc, query.FromUtc, query.ThroughUtc, 1, HasCompleteData ? 1 : 0,
                HasCompleteData, false, []), []);
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) =>
            [new("hash", "day.json", download.Provider, download.InstrumentId, download.Symbol, Day, download.SourceIntervalSeconds,
                download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds, download.FetchedAtUtc,
                new(download.RequestedFromUtc, download.RequestedThroughUtc, download.RequestedFromUtc, download.RequestedThroughUtc,
                    1, 1, !SaveHasGaps, false, []))];
        public MarketDataLibraryScan Scan() => new([], []);
        public HistoricalDataset Read(string datasetHash) => throw new NotSupportedException();
    }
}
