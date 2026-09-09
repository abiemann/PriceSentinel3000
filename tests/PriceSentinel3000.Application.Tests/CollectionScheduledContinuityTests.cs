using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionScheduledContinuityTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task DueScheduleUsesEachTickersFilesAndRepairsEarlierPartialSession()
    {
        var fixture = new Fixture(["NFLX", "SOFI"]);
        fixture.Seed("NFLX", "2026-09-03");
        fixture.Seed("NFLX", "2026-09-04", omitLast: 3);
        fixture.Seed("NFLX", "2026-09-08");
        fixture.Seed("SOFI", "2026-09-08");

        await fixture.RunUntilIdle();

        HistoricalDataRequest repaired = Assert.Single(fixture.Provider.Requests,
            r => r.Symbol == "NFLX" && RequestDay(r) == new DateOnly(2026, 9, 4));
        Assert.Equal(Window("2026-09-04").ThroughUtc.AddSeconds(-45), repaired.FromUtc);
        Assert.DoesNotContain(fixture.Provider.Requests, r => r.Symbol == "NFLX" && RequestDay(r) == new DateOnly(2026, 9, 3));
        Assert.DoesNotContain(fixture.Provider.Requests, r => RequestDay(r) == new DateOnly(2026, 9, 8));
        foreach (string symbol in new[] { "NFLX", "SOFI" })
        {
            HistoricalDataRequest[] today = fixture.Provider.Requests.Where(r => r.Symbol == symbol && RequestDay(r) == new DateOnly(2026, 9, 9)).ToArray();
            Assert.Equal(3, today.Length);
            Assert.Equal(fixture.Clock.Now, today.Max(r => r.ThroughUtc));
        }
        Assert.Equal(4800, fixture.ReadDay("NFLX", "2026-09-04").Candles.Count);
        Assert.True(fixture.ReadDay("NFLX", "2026-09-04").Coverage.Complete);
        Assert.All(fixture.Collector.State.Jobs.Where(j => j.SessionDate >= new DateOnly(2026, 9, 3)),
            j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        AssertRequestsAreFineChunks(fixture.Provider.Requests);
    }

    [Theory]
    [InlineData(CollectionJobStatus.Partial)]
    [InlineData(CollectionJobStatus.Unavailable)]
    [InlineData(CollectionJobStatus.Failed)]
    public async Task NextScheduledDayRetriesRecentIncompleteWorkWithoutRepeatingCompleteFiles(CollectionJobStatus firstResult)
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T20:15:00Z");
        fixture.Seed("NFLX", "2026-09-03");
        fixture.Seed("NFLX", "2026-09-04");
        fixture.Provider.Result = firstResult;
        fixture.Provider.ResultDate = new(2026, 9, 8);
        await fixture.RunUntilIdle();
        Assert.Equal(firstResult, fixture.Collector.State.Jobs.Single(j => j.SessionDate == new DateOnly(2026, 9, 8)).Status);
        DateTimeOffset retryStart = fixture.ReadDay("NFLX", "2026-09-08", fixture.Clock.Now).Coverage.Gaps[0].FromUtc;
        int firstRequests = fixture.Provider.Requests.Count;
        await fixture.Collector.TickAsync(true);
        Assert.Equal(firstRequests, fixture.Provider.Requests.Count);

        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-09T20:15:00Z");
        fixture.Provider.Result = CollectionJobStatus.Complete;
        await fixture.RunUntilIdle();

        HistoricalDataRequest[] retries = fixture.Provider.Requests.Skip(firstRequests)
            .Where(r => RequestDay(r) == new DateOnly(2026, 9, 8)).ToArray();
        Assert.NotEmpty(retries);
        Assert.Equal(retryStart, retries[0].FromUtc);
        Assert.DoesNotContain(fixture.Provider.Requests, r => RequestDay(r) is var day &&
            (day == new DateOnly(2026, 9, 3) || day == new DateOnly(2026, 9, 4)));
        Assert.True(fixture.ReadDay("NFLX", "2026-09-08").Coverage.Complete);
        Assert.Equal(CollectionJobStatus.Complete,
            fixture.Collector.State.Jobs.Single(j => j.SessionDate == new DateOnly(2026, 9, 8)).Status);
        AssertRequestsAreFineChunks(fixture.Provider.Requests);
    }

    [Fact]
    public async Task ReenablingResumesSavedHistoryIncludingSessionsBeforeNewEnableTime()
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Seed("NFLX", "2026-08-24");
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T18:00:00Z");
        await fixture.Collector.SaveSettingsAsync(fixture.Collector.State.Settings with { AutomaticDownloadsEnabled = false });
        await fixture.Collector.TickAsync(true);
        Assert.Empty(fixture.Provider.Requests);
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T19:00:00Z");
        await fixture.Collector.SaveSettingsAsync(fixture.Collector.State.Settings with { AutomaticDownloadsEnabled = true });
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T20:15:00Z");

        await fixture.RunUntilIdle();

        Assert.Contains(fixture.Provider.Requests, r => RequestDay(r) == new DateOnly(2026, 9, 3));
        Assert.Contains(fixture.Provider.Requests, r => RequestDay(r) == new DateOnly(2026, 9, 8));
        Assert.True(fixture.ReadDay("NFLX", "2026-09-03").Coverage.Complete);
        CollectionContinuityGap gap = Assert.Single(fixture.Collector.State.ContinuityGaps);
        Assert.Equal(new DateOnly(2026, 8, 25), gap.FromSessionDate);
        Assert.Equal(new DateOnly(2026, 8, 31), gap.ThroughSessionDate);
        AssertRequestsAreFineChunks(fixture.Provider.Requests);
    }

    [Fact]
    public async Task LongDowntimeRetainsCompactOldGapAcrossRestartAndDiscoversBrokerBoundary()
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Seed("NFLX", "2026-08-03");
        await fixture.RunUntilIdle();

        CollectionContinuityGap gap = Assert.Single(fixture.Store.Load().ContinuityGaps);
        Assert.Equal(new DateOnly(2026, 8, 4), gap.FromSessionDate);
        Assert.Equal(new DateOnly(2026, 9, 1), gap.ThroughSessionDate);
        Assert.Equal(new DateOnly(2026, 9, 2), fixture.Provider.Requests.Min(RequestDay));
        Assert.Equal(1, fixture.Collector.State.Jobs.Count(j => j.Status == CollectionJobStatus.Unavailable));
        Assert.InRange(fixture.Store.Load().Jobs.Count, 1, 20);
        int requests = fixture.Provider.Requests.Count;

        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        Assert.Equal(gap, Assert.Single(fixture.Collector.State.ContinuityGaps));
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        AssertRequestsAreFineChunks(fixture.Provider.Requests);
    }

    [Fact]
    public async Task RecordedOldGapSurvivesLossOfOriginalFileAndOperationalJobHistory()
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Seed("NFLX", "2026-08-03");
        await fixture.RunUntilIdle();
        CollectionContinuityGap gap = Assert.Single(fixture.Store.Load().ContinuityGaps);
        fixture.Library.Remove("NFLX", new(2026, 8, 3));
        fixture.Store.Save(fixture.Store.Load() with { Jobs = [] });
        fixture.Restart();
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-10T20:15:00Z");
        int before = fixture.Provider.Requests.Count;

        await fixture.RunUntilIdle();

        CollectionContinuityGap retained = Assert.Single(fixture.Collector.State.ContinuityGaps);
        Assert.Equal(gap.FromSessionDate, retained.FromSessionDate);
        Assert.True(retained.ThroughSessionDate >= gap.ThroughSessionDate);
        Assert.DoesNotContain(fixture.Provider.Requests.Skip(before), r => RequestDay(r) == new DateOnly(2026, 9, 3));
        Assert.Contains(fixture.Provider.Requests.Skip(before), r => RequestDay(r) == new DateOnly(2026, 9, 10));
        Assert.Equal(CollectionJobStatus.Complete,
            fixture.Collector.State.Jobs.Single(j => j.SessionDate == new DateOnly(2026, 9, 10)).Status);
    }

    [Fact]
    public async Task LegacyPendingFallbackIsRestartedAt15SecondsOnly()
    {
        var fixture = new Fixture(["NFLX"], automatic: false);
        fixture.Store.Save(fixture.Store.Load() with
        {
            Jobs = [new() { Symbol = "NFLX", SessionDate = new(2026, 9, 8),
                LibraryRootPath = fixture.Library.RootPath, QueuedAtUtc = fixture.Clock.Now,
                NextSourceIntervalSeconds = 30, ActualSourceIntervalSeconds = 30 }],
        });
        fixture.Restart();

        await fixture.Collector.TickAsync(true);

        Assert.Equal(15, Assert.Single(fixture.Provider.Requests).SourceIntervalSeconds);
        CollectionJob job = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        Assert.Equal(15, job.NextSourceIntervalSeconds);
        Assert.Equal(15, job.ActualSourceIntervalSeconds);
    }

    [Fact]
    public async Task DeletedFileIsDownloadedAgainDespiteCompletedJobInJournal()
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-04T20:15:00Z");
        fixture.Seed("NFLX", "2026-09-03");
        await fixture.RunUntilIdle();
        CollectionJob previous = fixture.Collector.State.Jobs.Single(j => j.SessionDate == new DateOnly(2026, 9, 4));
        Assert.Equal(CollectionJobStatus.Complete, previous.Status);
        fixture.Library.Remove("NFLX", previous.SessionDate);
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T20:15:00Z");
        int before = fixture.Provider.Requests.Count;

        await fixture.RunUntilIdle();

        HistoricalDataRequest[] repaired = fixture.Provider.Requests.Skip(before)
            .Where(r => RequestDay(r) == previous.SessionDate).ToArray();
        Assert.Equal(5, repaired.Length);
        Assert.Equal(Window("2026-09-04").FromUtc, repaired[0].FromUtc);
        Assert.Equal(Window("2026-09-04").ThroughUtc, repaired[^1].ThroughUtc);
        Assert.Equal(previous.Id, fixture.Collector.State.Jobs.Single(j => j.SessionDate == previous.SessionDate).Id);
        Assert.True(fixture.ReadDay("NFLX", "2026-09-04").Coverage.Complete);
        Assert.DoesNotContain(fixture.Provider.Requests, r => RequestDay(r) == new DateOnly(2026, 9, 3));
    }

    private static DateOnly RequestDay(HistoricalDataRequest request) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, Eastern).DateTime);
    private static CollectionSessionWindow Window(string day) => CollectionSchedule.GetSessionWindow(DateOnly.Parse(day), "24_5");
    private static void AssertRequestsAreFineChunks(IEnumerable<HistoricalDataRequest> requests) => Assert.All(requests, request =>
    {
        Assert.Equal(15, request.SourceIntervalSeconds);
        Assert.Equal("24_5", request.SessionBounds);
        Assert.InRange(request.ThroughUtc - request.FromUtc, TimeSpan.FromSeconds(15), TimeSpan.FromHours(6));
    });

    private static HistoricalDownload Download(HistoricalDataRequest request, int omitLast = 0)
    {
        int count = (int)(request.ThroughUtc - request.FromUtc).TotalSeconds / request.SourceIntervalSeconds;
        HistoricalCandle[] candles = Enumerable.Range(0, Math.Max(0, count - omitLast)).Select(i =>
        {
            DateTimeOffset start = request.FromUtc.AddSeconds(i * request.SourceIntervalSeconds);
            return new HistoricalCandle(start, start.AddSeconds(request.SourceIntervalSeconds),
                start.AddSeconds(request.SourceIntervalSeconds), 10, 11, 9, 10, 100);
        }).ToArray();
        return new("Robinhood", "id-" + request.Symbol, request.Symbol, request.SourceIntervalSeconds,
            request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds,
            request.ThroughUtc.AddMinutes(15), request.FromUtc, request.ThroughUtc, candles);
    }

    private sealed class Fixture
    {
        public MemoryStore Store { get; } = new();
        public Provider Provider { get; } = new();
        public Library Library { get; } = new();
        public Clock Clock { get; } = new();
        public MarketDataCollector Collector { get; private set; }

        public Fixture(string[] symbols, bool automatic = true)
        {
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath, TimeZoneId = "America/Los_Angeles",
                AutomaticDownloadsEnabled = automatic,
                AutomaticEnabledAtUtc = automatic ? DateTimeOffset.Parse("2026-08-01T00:00:00Z") : null,
                Lists = [new(Guid.NewGuid(), "Selected", true, symbols.Select(s => new DownloadListMember(s)).ToArray())],
            } });
            Collector = CreateCollector();
        }

        public void Seed(string symbol, string day, int omitLast = 0) =>
            Library.Save(Download(new(symbol, Window(day).FromUtc, Window(day).ThroughUtc, SessionBounds: "24_5"), omitLast));
        public HistoricalDataQueryResult ReadDay(string symbol, string day, DateTimeOffset? through = null) =>
            Library.Query(new(symbol, Window(day).FromUtc, through ?? Window(day).ThroughUtc,
                SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage, IncludeCompatibleSessions: true));
        public void Restart() => Collector = CreateCollector();
        public async Task RunUntilIdle()
        {
            for (int i = 0; i < 100; i++)
                if (await Collector.TickAsync(true) == CollectionBatchResult.Idle) return;
            Assert.Fail("Collection did not settle after 100 ticks.");
        }
        private MarketDataCollector CreateCollector() => new(Store, Provider, _ => Library, Clock,
            new() { MaximumRequestsPerTick = 100, MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero });
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-09T20:15:00Z");
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
        public CollectionJobStatus Result { get; set; } = CollectionJobStatus.Complete;
        public DateOnly? ResultDate { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            CollectionJobStatus result = RequestDay(request) < new DateOnly(2026, 9, 3)
                ? CollectionJobStatus.Unavailable : ResultDate is null || RequestDay(request) == ResultDate ? Result : CollectionJobStatus.Complete;
            if (result == CollectionJobStatus.Failed) throw new InvalidDataException("Provider data is temporarily invalid.");
            HistoricalDownload download = Download(request, result == CollectionJobStatus.Partial ? 1 : 0);
            if (result == CollectionJobStatus.Unavailable) download = download with { Candles = [] };
            return Task.FromResult(download);
        }
    }

    private sealed class Library : IMarketDataLibrary
    {
        public string RootPath => Path.GetFullPath("scheduled-continuity-test-library");
        private readonly List<HistoricalDataset> _files = [];
        public void Remove(string symbol, DateOnly date) => _files.RemoveAll(d => d.Symbol == symbol && d.TradingDate == date);
        public MarketDataLibraryScan Scan() => new(_files.Select(Describe).ToArray(), []);
        public HistoricalDataset Read(string hash) => _files.Single(d => d.DatasetHash == hash);

        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(download.RequestedFromUtc, Eastern).DateTime);
            var dataset = new HistoricalDataset(1, "America/New_York", Guid.NewGuid().ToString("N"), download.Provider,
                download.InstrumentId, download.Symbol, day, download.SourceIntervalSeconds,
                download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds, download.FetchedAtUtc,
                Coverage(download.RequestedFromUtc, download.RequestedThroughUtc, download.SourceIntervalSeconds, download.Candles),
                download.Candles);
            _files.Add(dataset);
            return [Describe(dataset)];
        }

        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            HistoricalDataset[] datasets = _files.Where(d => d.Symbol == query.Symbol &&
                d.SourceIntervalSeconds == query.SourceIntervalSeconds && query.MatchesSessionBounds(d.SessionBounds) &&
                (query.AdjustmentPolicy is null || d.AdjustmentPolicy == query.AdjustmentPolicy) &&
                (query.AdjustmentBasis is null || d.AdjustmentBasis == query.AdjustmentBasis) &&
                d.Coverage.RequestedFromUtc < query.ThroughUtc && d.Coverage.RequestedThroughUtc > query.FromUtc).ToArray();
            HistoricalCandle[] candles = datasets.SelectMany(d => d.Candles)
                .Where(c => c.StartsAtUtc >= query.FromUtc && c.EndsAtUtc <= query.ThroughUtc)
                .Distinct().OrderBy(c => c.StartsAtUtc).ToArray();
            return new(true, datasets.Select(Describe).ToArray(), candles,
                Coverage(query.FromUtc, query.ThroughUtc, query.SourceIntervalSeconds, candles), []);
        }

        private static HistoricalDatasetInfo Describe(HistoricalDataset d) => new(d.DatasetHash, "copied/" + d.DatasetHash,
            d.Provider, d.InstrumentId, d.Symbol, d.TradingDate, d.SourceIntervalSeconds,
            d.AdjustmentPolicy, d.AdjustmentBasis, d.SessionBounds, d.FetchedAtUtc, d.Coverage);

        private static HistoricalCoverage Coverage(DateTimeOffset from, DateTimeOffset through, int interval,
            IReadOnlyList<HistoricalCandle> candles)
        {
            int expected = (int)(through - from).TotalSeconds / interval;
            var gaps = new List<HistoricalGap>();
            DateTimeOffset cursor = from;
            foreach (HistoricalCandle candle in candles)
            {
                if (candle.StartsAtUtc > cursor) gaps.Add(new(cursor, candle.StartsAtUtc));
                if (candle.EndsAtUtc > cursor) cursor = candle.EndsAtUtc;
            }
            if (cursor < through) gaps.Add(new(cursor, through));
            return new(from, through, candles.Count == 0 ? null : candles[0].StartsAtUtc,
                candles.Count == 0 ? null : candles[^1].EndsAtUtc, expected, candles.Count,
                gaps.Count == 0, candles.Count > 0 && candles.All(c => c.Volume is not null), gaps);
        }
    }
}
