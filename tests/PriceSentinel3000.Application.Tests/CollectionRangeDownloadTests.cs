using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed partial class MarketDataCollectorTests
{
    [Fact]
    public async Task SelectedRangeDownloadsOnlyMissingCandlesAndDoesNotDiscoverOtherDays()
    {
        var (collector, _, provider, library, _) = Create();
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-04T14:00:00Z");
        DateTimeOffset through = from.AddMinutes(30);
        // Reuse an already-saved middle section; surrounding data must remain intact.
        foreach (var range in new[] { (from.AddMinutes(-15), from), (from.AddMinutes(10), from.AddMinutes(20)), (through, through.AddMinutes(15)) })
            library.Save(await provider.DownloadHistoryAsync(new("SOFI", range.Item1, range.Item2, 15, "24_5", "split"), default));
        provider.Requests.Clear();

        Guid id = Assert.Single(await collector.QueueRangeAsync("SOFI", from, through));
        await DrainRangeAsync(collector);

        Assert.Equal(new[] { (from, from.AddMinutes(10)), (from.AddMinutes(20), through) },
            provider.Requests.Select(request => (request.FromUtc, request.ThroughUtc)));
        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(id, job.Id);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        Assert.Equal(100m, job.SavedCoveragePercent);
        Assert.False(job.IsAvailabilityProbe);
        Assert.Null(job.DiscoveryEmptySessions);
        Assert.Null(job.DiscoveryAsOfDate);
        HistoricalDataQueryResult saved = library.Query(new("SOFI", from.AddMinutes(-15), through.AddMinutes(15),
            SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage));
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(240, saved.Candles.Count);
    }

    [Fact]
    public async Task SelectedRangeClipsToCompletedCandlesAndPersistsItsBoundariesAcrossRestart()
    {
        var (collector, store, provider, library, clock) = Create();
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-09T22:00:00Z");
        clock.Now = from.AddMinutes(3).AddSeconds(53);
        await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15));
        var restarted = new MarketDataCollector(store, provider, _ => library, clock, Options());

        await DrainRangeAsync(restarted);

        HistoricalDataRequest request = Assert.Single(provider.Requests);
        Assert.Equal(from, request.FromUtc);
        Assert.Equal(from.AddMinutes(3).AddSeconds(45), request.ThroughUtc);
        CollectionJob job = Assert.Single(restarted.State.Jobs);
        Assert.Equal(from, job.RequestedFromUtc);
        Assert.Equal(request.ThroughUtc, job.RequestedThroughUtc);
        Assert.Equal(15, library.Query(new("SOFI", from, request.ThroughUtc, SessionBounds: "24_5")).Candles.Count);
    }

    [Fact]
    public async Task SelectedRangeAcrossEasternMidnightCreatesSeparateDailyJobsWithoutLosingBoundaryCandles()
    {
        var (collector, _, provider, library, clock) = Create();
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-09T03:45:00Z");
        DateTimeOffset midnight = DateTimeOffset.Parse("2026-09-09T04:00:00Z");
        clock.Now = midnight.AddHours(10);
        IReadOnlyList<Guid> ids = await collector.QueueRangeAsync("SOFI", from, midnight.AddMinutes(15));

        await DrainRangeAsync(collector);

        Assert.Equal(2, ids.Count);
        Assert.Equal(new[] { (from, midnight), (midnight, midnight.AddMinutes(15)) },
            provider.Requests.OrderBy(request => request.FromUtc).Select(request => (request.FromUtc, request.ThroughUtc)));
        Assert.Equal(new[] { new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 9) },
            library.Scan().Datasets.OrderBy(dataset => dataset.TradingDate).Select(dataset => dataset.TradingDate));
        HistoricalDataQueryResult saved = library.Query(new("SOFI", from, midnight.AddMinutes(15), SessionBounds: "24_5"));
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(120, saved.Candles.Count);
        Assert.Equal(120, saved.Candles.Select(candle => candle.StartsAtUtc).Distinct().Count());
    }

    [Fact]
    public async Task SelectedRangeOverridesKnownEmptyAndExplicitlyRetriesUnavailableData()
    {
        var (_, store, provider, library, clock) = Create();
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-04T14:00:00Z");
        var gaps = new RangeGapIndex(new(from, from.AddMinutes(15)));
        var collector = new MarketDataCollector(store, provider, _ => library, clock, Options(), _ => gaps);
        provider.EmptyIntervals.Add(15);
        Guid id = Assert.Single(await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15)));

        await DrainRangeAsync(collector);

        Assert.Single(provider.Requests);
        Assert.True(gaps.QueryCount > 0);
        Assert.Equal(CollectionJobStatus.Unavailable, Assert.Single(collector.State.Jobs).Status);
        Assert.Single(gaps.Attempts);
        Assert.False(gaps.Attempts[0]);
        provider.EmptyIntervals.Clear();
        Assert.Equal(id, Assert.Single(await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15))));
        await DrainRangeAsync(collector);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(collector.State.Jobs).Status);
        Assert.True(gaps.Attempts[1]);
    }

    [Fact]
    public async Task SelectedRangeTransientFailureRetriesTheSameRangeWithoutRecordingNoData()
    {
        var (_, store, provider, library, clock) = Create();
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-04T14:00:00Z");
        var gaps = new RangeGapIndex(new(from, from.AddMinutes(15)));
        var collector = new MarketDataCollector(store, provider, _ => library, clock, Options(), _ => gaps);
        provider.FailingSymbol = "SOFI";
        await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15));
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Pending, Assert.Single(collector.State.Jobs).Status);
        Assert.Empty(gaps.Attempts);

        provider.FailingSymbol = null;
        await DrainRangeAsync(collector);

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(provider.Requests[0], provider.Requests[1]);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(collector.State.Jobs).Status);
        Assert.True(Assert.Single(gaps.Attempts));
    }

    [Fact]
    public async Task SelectedRangeDoesNotReplaceFullDayWorkOrDuplicateItsOwnPendingWork()
    {
        var (collector, _, _, _, _) = Create();
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-04T14:00:00Z");
        await collector.QueueManualAsync(["SOFI"], Day, Day, "24_5");
        Guid fullDay = Assert.Single(collector.State.Jobs).Id;
        Guid target = Assert.Single(await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15)));
        Assert.Equal(target, Assert.Single(await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15))));
        await collector.QueueManualAsync(["SOFI"], Day, Day, "24_5");

        Assert.NotEqual(fullDay, target);
        Assert.Equal(2, collector.State.Jobs.Count);
        Assert.Null(collector.State.Jobs.Single(job => job.Id == fullDay).RequestedFromUtc);
        Assert.Equal(from, collector.State.Jobs.Single(job => job.Id == target).RequestedFromUtc);
    }

    [Fact]
    public async Task SelectedRangeDoesNotQueueFutureOrClosedMarketTime()
    {
        var (collector, _, provider, _, clock) = Create();
        DateTimeOffset future = clock.Now.AddHours(1);
        Assert.Empty(await collector.QueueRangeAsync("SOFI", future, future.AddMinutes(15)));
        // Labor Day remains closed until its evening overnight session.
        DateTimeOffset closed = DateTimeOffset.Parse("2026-09-07T14:00:00Z");
        Assert.Empty(await collector.QueueRangeAsync("SOFI", closed, closed.AddMinutes(15)));
        Assert.Empty(collector.State.Jobs);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task CompletingSelectedRangeDoesNotClearFullDayContinuityGap()
    {
        var (_, store, provider, library, clock) = Create();
        store.Save(store.Load() with
        {
            ContinuityGaps = [new("SOFI", Day, Day, "24_5", Settings().LibraryRootPath)],
        });
        var collector = new MarketDataCollector(store, provider, _ => library, clock, Options());
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-04T14:00:00Z");
        await collector.QueueRangeAsync("SOFI", from, from.AddMinutes(15));
        await DrainRangeAsync(collector);
        Assert.Single(collector.State.ContinuityGaps);
    }

    private static async Task DrainRangeAsync(MarketDataCollector collector)
    {
        for (int tick = 0; tick < 20; tick++)
            if (await collector.TickAsync(true) == CollectionBatchResult.Idle) return;
        Assert.Fail("Selected-range collection did not finish in 20 ticks.");
    }

    private sealed class RangeGapIndex(HistoricalGap known) : ICollectionGapIndex
    {
        public int QueryCount { get; private set; }
        public List<bool> Attempts { get; } = [];
        public void Initialize() { }
        public CollectionGapSnapshot Query(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset nowUtc)
        {
            QueryCount++;
            return new([known], false);
        }
        public void RecordAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
            IReadOnlyList<HistoricalGap> unavailableRanges, bool receivedCandles, DateTimeOffset checkedAtUtc,
            DateTimeOffset? retryAfterUtc) => Attempts.Add(receivedCandles);
    }
}