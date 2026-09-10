using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed partial class MarketDataCollectorTests
{
    [Fact]
    public async Task RestartRefreshesStoppedJobCoverageAndClockProjectionDoesNotWriteOrDownload()
    {
        var (_, store, provider, library, clock) = Create();
        DateOnly day = new(2026, 9, 9);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc;
        SaveCacheCandles(library, start, start.AddHours(6));
        CollectionJob stopped = new()
        {
            Symbol = "SOFI", SessionDate = day, SessionBounds = "24_5",
            LibraryRootPath = library.RootPath, Status = CollectionJobStatus.Partial,
            SavedCoveragePercent = 25m,
        };
        store.Save(store.Load() with { Jobs = [stopped] });
        clock.Now = start.AddHours(12);
        var restarted = new MarketDataCollector(store, provider, _ => library, clock, Options());

        await restarted.TickAsync(false);

        Assert.Equal(50m, Assert.Single(restarted.State.Jobs).SavedCoveragePercent);
        Assert.Equal(50m, restarted.GetSavedCoverage(stopped, clock.Now));
        string persisted = JsonSerializer.Serialize(store.Load());
        string state = JsonSerializer.Serialize(restarted.State);
        clock.Now = start.AddHours(18);

        Assert.Equal(100m / 3, restarted.GetSavedCoverage(stopped, clock.Now));
        Assert.Equal(persisted, JsonSerializer.Serialize(store.Load()));
        Assert.Equal(state, JsonSerializer.Serialize(restarted.State));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task CompletedLibraryScanRefreshesOnlyJobsPinnedToThatFolder()
    {
        var (_, store, provider, _, clock) = Create();
        DateOnly day = new(2026, 9, 9);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc;
        var first = new CoverageCacheLibrary(Path.GetFullPath("first-coverage-library"));
        var second = new CoverageCacheLibrary(Path.GetFullPath("second-coverage-library"));
        SaveCacheCandles(first, start, start.AddHours(6));
        SaveCacheCandles(second, start, start.AddHours(3));
        CollectionJob firstJob = new()
        {
            Symbol = "SOFI", SessionDate = day, SessionBounds = "24_5",
            LibraryRootPath = first.RootPath, Status = CollectionJobStatus.Partial,
        };
        CollectionJob secondJob = firstJob with { Id = Guid.NewGuid(), LibraryRootPath = second.RootPath };
        store.Save(store.Load() with
        {
            Settings = Settings() with { LibraryRootPath = second.RootPath },
            Jobs = [firstJob, secondJob],
        });
        clock.Now = start.AddHours(12);
        var collector = new MarketDataCollector(store, provider,
            root => root == first.RootPath ? first : second, clock, Options());
        await collector.TickAsync(false);
        Assert.Equal(50m, collector.GetSavedCoverage(firstJob, clock.Now));
        Assert.Equal(25m, collector.GetSavedCoverage(secondJob, clock.Now));
        SaveCacheCandles(first, start.AddHours(6), start.AddHours(9));

        MarketDataLibraryScan scan = await collector.ScanLibraryAsync(first);

        Assert.Equal(2, scan.Datasets.Count);
        Assert.Equal(75m, collector.GetSavedCoverage(firstJob, clock.Now));
        Assert.Equal(25m, collector.GetSavedCoverage(secondJob, clock.Now));
        Assert.Equal(75m, collector.State.Jobs.Single(job => job.Id == firstJob.Id).SavedCoveragePercent);
        Assert.Equal(25m, collector.State.Jobs.Single(job => job.Id == secondJob.Id).SavedCoveragePercent);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task SuccessfulCollectionRefreshesPreviouslyEmptyCoverageCache()
    {
        var (collector, _, provider, _, clock) = Create();
        await collector.QueueManualAsync(["SOFI"], Day, Day);
        await collector.TickAsync(false);
        CollectionJob queued = Assert.Single(collector.State.Jobs);
        Assert.Equal(0m, collector.GetSavedCoverage(queued, clock.Now));

        await collector.TickAsync(true);

        Assert.Single(provider.Requests);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(collector.State.Jobs).Status);
        Assert.Equal(100m, collector.GetSavedCoverage(queued, clock.Now));
    }

    [Fact]
    public async Task SelectedRangeCollectionRefreshesRetainedDailyCoverageForTheSameStock()
    {
        var (_, store, provider, library, clock) = Create();
        DateOnly day = new(2026, 9, 9);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc;
        SaveCacheCandles(library, start, start.AddHours(6));
        CollectionJob daily = new()
        {
            Symbol = "SOFI", SessionDate = day, SessionBounds = "24_5",
            LibraryRootPath = library.RootPath, Status = CollectionJobStatus.Partial,
        };
        store.Save(store.Load() with { Jobs = [daily] });
        clock.Now = start.AddHours(12);
        var collector = new MarketDataCollector(store, provider, _ => library, clock, Options());
        await collector.TickAsync(false);
        Assert.Equal(50m, collector.GetSavedCoverage(daily, clock.Now));
        Guid rangeId = Assert.Single(await collector.QueueRangeAsync("SOFI", start.AddHours(6), start.AddHours(9)));

        await collector.TickAsync(true);

        Assert.Single(provider.Requests);
        CollectionJob range = collector.State.Jobs.Single(job => job.Id == rangeId);
        Assert.Equal(100m, collector.GetSavedCoverage(range, clock.Now));
        Assert.Equal(75m, collector.GetSavedCoverage(daily, clock.Now));
        Assert.Equal(CollectionJobStatus.Partial, collector.State.Jobs.Single(job => job.Id == daily.Id).Status);

        await collector.TickAsync(true);

        Assert.Equal(CollectionJobStatus.Complete, collector.State.Jobs.Single(job => job.Id == rangeId).Status);
        Assert.Single(provider.Requests);
        Assert.Equal(75m, collector.GetSavedCoverage(daily, clock.Now));
    }

    private static void SaveCacheCandles(IMarketDataLibrary library, DateTimeOffset from, DateTimeOffset through)
    {
        HistoricalCandle[] candles = Enumerable.Range(0, (int)((through - from).TotalSeconds / 15))
            .Select(index =>
            {
                DateTimeOffset start = from.AddSeconds(index * 15);
                return new HistoricalCandle(start, start.AddSeconds(15), start.AddSeconds(15), 10, 11, 9, 10, null);
            }).ToArray();
        library.Save(new("test", "instrument", "SOFI", 15, "split", "robinhood-split-unversioned",
            "24_5", through, from, through, candles));
    }

    private sealed class CoverageCacheLibrary(string root) : IMarketDataLibrary
    {
        private readonly Library _inner = new();
        public string RootPath => root;
        public MarketDataLibraryScan Scan() => _inner.Scan();
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => _inner.Save(download);
        public HistoricalDataset Read(string datasetHash) => _inner.Read(datasetHash);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => _inner.Query(query);
    }
}
