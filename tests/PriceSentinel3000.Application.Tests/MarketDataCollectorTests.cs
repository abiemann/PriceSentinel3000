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
        for (int i = 0; i < 100; i++)
        {
            if (await restarted.TickAsync(true) == CollectionBatchResult.Idle) break;
            Assert.True(i < 99, "Queued collection did not settle after 100 ticks.");
        }
        Assert.Equal(new[] { "SOFI", "NVDA" }, provider.Requests.Select(r => r.Symbol).Distinct());
        Assert.Empty(provider.Requests.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Equal(restarted.State.Jobs.Count, restarted.State.Jobs.Select(j => (j.Symbol, j.SessionDate, j.SessionBounds)).Distinct().Count());
        Assert.All(collector.State.Jobs, queued => Assert.Contains(restarted.State.Jobs, j => j.Id == queued.Id));
        CollectionJob[] boundaryJobs = restarted.State.Jobs.Where(j => j.SessionDate == new DateOnly(2026, 8, 31)).ToArray();
        Assert.Equal(new[] { "SOFI", "NVDA" }, boundaryJobs.Select(j => j.Symbol));
        Assert.All(boundaryJobs, job =>
        {
            Assert.Equal(CollectionJobStatus.Partial, job.Status);
            HistoricalDataQueryResult saved = library.Query(new(job.Symbol,
                DateTimeOffset.Parse("2026-08-31T04:00:00Z"), DateTimeOffset.Parse("2026-09-01T04:00:00Z"),
                SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage));
            Assert.Equal(960, saved.Candles.Count);
            Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z"), saved.Candles[0].StartsAtUtc);
            Assert.Equal(DateTimeOffset.Parse("2026-09-01T04:00:00Z"), saved.Candles[^1].EndsAtUtc);
            Assert.False(saved.Coverage.Complete);
            Assert.Equal(new HistoricalGap(DateTimeOffset.Parse("2026-08-31T04:00:00Z"),
                DateTimeOffset.Parse("2026-09-01T00:00:00Z")), Assert.Single(saved.Coverage.Gaps));
        });
        Assert.All(restarted.State.Jobs.Where(j => j.SessionDate != new DateOnly(2026, 8, 31)),
            j => Assert.True(j.Status is CollectionJobStatus.Complete or CollectionJobStatus.Unavailable));
        Assert.All(provider.Requests, r =>
        {
            Assert.Equal("24_5", r.SessionBounds);
            Assert.Equal(15, r.SourceIntervalSeconds);
            Assert.InRange(r.ThroughUtc - r.FromUtc, TimeSpan.FromSeconds(15), TimeSpan.FromHours(6));
        });
        int requests = provider.Requests.Count;
        Assert.True(requests > collector.State.Jobs.Count);
        await restarted.TickAsync(true);
        Assert.Equal(requests, provider.Requests.Count);
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
                Enumerable.Range(0, (int)((request.ThroughUtc - request.FromUtc).TotalSeconds / request.SourceIntervalSeconds))
                    .Select(i =>
                    {
                        DateTimeOffset start = request.FromUtc.AddSeconds(i * request.SourceIntervalSeconds);
                        return new HistoricalCandle(start, start.AddSeconds(request.SourceIntervalSeconds),
                            start.AddSeconds(request.SourceIntervalSeconds), 10, 11, 9, 10, null);
                    }).Where(candle => candle.StartsAtUtc >= DateTimeOffset.Parse("2026-09-01T00:00:00Z")).ToArray();
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
        private readonly List<HistoricalDataset> _files = [];

        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            if (HasCompleteData && !_files.Any(d => d.Symbol == query.Symbol))
            {
                HistoricalCandle[] seeded = Enumerable.Range(0, (int)((query.ThroughUtc - query.FromUtc).TotalSeconds / query.SourceIntervalSeconds))
                    .Select(i =>
                    {
                        DateTimeOffset start = query.FromUtc.AddSeconds(i * query.SourceIntervalSeconds);
                        return new HistoricalCandle(start, start.AddSeconds(query.SourceIntervalSeconds),
                            start.AddSeconds(query.SourceIntervalSeconds), 10, 11, 9, 10, null);
                    }).ToArray();
                Save(new("test", "instrument", query.Symbol, query.SourceIntervalSeconds, "split", "robinhood-split-unversioned",
                    query.SessionBounds ?? "regular", query.ThroughUtc, query.FromUtc, query.ThroughUtc, seeded));
            }
            HistoricalDataset[] datasets = _files.Where(d => d.Symbol == query.Symbol && d.SourceIntervalSeconds == query.SourceIntervalSeconds &&
                query.MatchesSessionBounds(d.SessionBounds) && d.Coverage.RequestedFromUtc < query.ThroughUtc &&
                d.Coverage.RequestedThroughUtc > query.FromUtc).ToArray();
            HistoricalCandle[] candles = datasets.SelectMany(d => d.Candles)
                .Where(c => c.StartsAtUtc >= query.FromUtc && c.EndsAtUtc <= query.ThroughUtc)
                .Distinct().OrderBy(c => c.StartsAtUtc).ToArray();
            return new(true, datasets.Select(Describe).ToArray(), candles,
                Coverage(query.FromUtc, query.ThroughUtc, query.SourceIntervalSeconds, candles), []);
        }
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
        {
            HistoricalCandle[] candles = (SaveHasGaps ? download.Candles.SkipLast(1) : download.Candles).ToArray();
            DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(download.RequestedFromUtc,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);
            var dataset = new HistoricalDataset(1, "America/New_York", Guid.NewGuid().ToString("N"), download.Provider,
                download.InstrumentId, download.Symbol, day, download.SourceIntervalSeconds, download.AdjustmentPolicy,
                download.AdjustmentBasis, download.SessionBounds, download.FetchedAtUtc,
                Coverage(download.RequestedFromUtc, download.RequestedThroughUtc, download.SourceIntervalSeconds, candles), candles);
            _files.Add(dataset);
            return [Describe(dataset)];
        }
        public MarketDataLibraryScan Scan() => new(_files.Select(Describe).ToArray(), []);
        public HistoricalDataset Read(string datasetHash) => _files.Single(d => d.DatasetHash == datasetHash);
        private static HistoricalDatasetInfo Describe(HistoricalDataset d) => new(d.DatasetHash, d.DatasetHash + ".json",
            d.Provider, d.InstrumentId, d.Symbol, d.TradingDate, d.SourceIntervalSeconds, d.AdjustmentPolicy,
            d.AdjustmentBasis, d.SessionBounds, d.FetchedAtUtc, d.Coverage);
        private static HistoricalCoverage Coverage(DateTimeOffset from, DateTimeOffset through, int interval,
            IReadOnlyList<HistoricalCandle> candles)
        {
            var gaps = new List<HistoricalGap>();
            DateTimeOffset cursor = from;
            foreach (HistoricalCandle candle in candles)
            {
                if (candle.StartsAtUtc > cursor) gaps.Add(new(cursor, candle.StartsAtUtc));
                if (candle.EndsAtUtc > cursor) cursor = candle.EndsAtUtc;
            }
            if (cursor < through) gaps.Add(new(cursor, through));
            return new(from, through, candles.Count > 0 ? candles[0].StartsAtUtc : null,
                candles.Count > 0 ? candles[^1].EndsAtUtc : null, (int)((through - from).TotalSeconds / interval),
                candles.Count, gaps.Count == 0, false, gaps);
        }
    }
}
