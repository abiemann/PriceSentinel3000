using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionScheduledContinuityTests
{
    [Fact]
    public async Task DueScheduleUsesEachTickersFilesAndRepairsEarlierPartialSession()
    {
        var fixture = new Fixture(["NFLX", "SOFI"]);
        fixture.Seed("NFLX", "2026-09-03");
        fixture.Seed("NFLX", "2026-09-04", omitLast: 3);
        fixture.Seed("NFLX", "2026-09-08");
        fixture.Seed("SOFI", "2026-09-08");

        await fixture.Collector.TickAsync(true);

        Assert.Equal(new[] { "NFLX:2026-09-04", "NFLX:2026-09-09", "SOFI:2026-09-09" },
            fixture.Provider.Requests.Select(Key));
        HistoricalDataRequest repaired = fixture.Provider.Requests[0];
        Assert.Equal(Window("2026-09-04").ThroughUtc.AddSeconds(-45), repaired.FromUtc);
        Assert.All(fixture.Provider.Requests, r => Assert.Equal(15, r.SourceIntervalSeconds));
        Assert.All(fixture.Collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        Assert.Equal(1560, fixture.Library.Query(new("NFLX", Window("2026-09-04").FromUtc,
            Window("2026-09-04").ThroughUtc)).Candles.Count);
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
        await fixture.Collector.TickAsync(true);
        Assert.Equal(firstResult, Assert.Single(fixture.Collector.State.Jobs).Status);
        await fixture.Collector.TickAsync(true);
        Assert.Single(fixture.Provider.Requests);

        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-09T20:15:00Z");
        fixture.Provider.Result = CollectionJobStatus.Complete;
        await fixture.Collector.TickAsync(true);

        Assert.Equal(new[] { "NFLX:2026-09-08", "NFLX:2026-09-08", "NFLX:2026-09-09" },
            fixture.Provider.Requests.Select(Key));
        DateTimeOffset expectedRetryStart = firstResult == CollectionJobStatus.Partial
            ? Window("2026-09-08").ThroughUtc.AddSeconds(-15) : Window("2026-09-08").FromUtc;
        Assert.Equal(expectedRetryStart, fixture.Provider.Requests[1].FromUtc);
        Assert.All(fixture.Collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        Assert.All(fixture.Provider.Requests, r => Assert.Equal(15, r.SourceIntervalSeconds));
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

        await fixture.Collector.TickAsync(true);

        Assert.Equal(new[] { "NFLX:2026-09-02", "NFLX:2026-09-03", "NFLX:2026-09-04", "NFLX:2026-09-08" },
            fixture.Provider.Requests.Select(Key));
        CollectionContinuityGap gap = Assert.Single(fixture.Collector.State.ContinuityGaps);
        Assert.Equal(new DateOnly(2026, 8, 25), gap.FromSessionDate);
        Assert.Equal(new DateOnly(2026, 9, 1), gap.ThroughSessionDate);
    }

    [Fact]
    public async Task LongDowntimeRetainsCompactOldGapAcrossRestartAndRequestsOnlyRecent15SecondSessions()
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Seed("NFLX", "2026-08-03");
        await fixture.Collector.TickAsync(true);

        Assert.Equal(new[] { "NFLX:2026-09-03", "NFLX:2026-09-04", "NFLX:2026-09-08", "NFLX:2026-09-09" },
            fixture.Provider.Requests.Select(Key));
        CollectionContinuityGap gap = Assert.Single(fixture.Store.Load().ContinuityGaps);
        Assert.Equal(new DateOnly(2026, 8, 4), gap.FromSessionDate);
        Assert.Equal(new DateOnly(2026, 9, 2), gap.ThroughSessionDate);
        Assert.Equal(4, fixture.Store.Load().Jobs.Count);

        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        Assert.Equal(gap, Assert.Single(fixture.Collector.State.ContinuityGaps));
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.All(fixture.Provider.Requests, r => Assert.Equal(15, r.SourceIntervalSeconds));
    }

    [Fact]
    public async Task RecordedOldGapSurvivesLossOfOriginalFileAndOperationalJobHistory()
    {
        var fixture = new Fixture(["NFLX"]);
        fixture.Seed("NFLX", "2026-08-03");
        await fixture.Collector.TickAsync(true);
        CollectionContinuityGap gap = Assert.Single(fixture.Store.Load().ContinuityGaps);
        fixture.Library.Files.Remove(("NFLX", new(2026, 8, 3)));
        fixture.Store.Save(fixture.Store.Load() with { Jobs = [] });
        fixture.Restart();
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-10T20:15:00Z");

        await fixture.Collector.TickAsync(true);

        Assert.Equal(gap, Assert.Single(fixture.Collector.State.ContinuityGaps));
        Assert.Equal("NFLX:2026-09-10", Key(fixture.Provider.Requests[^1]));
        Assert.Equal(5, fixture.Provider.Requests.Count);
        Assert.Equal(new DateOnly(2026, 9, 10), Assert.Single(fixture.Collector.State.Jobs).SessionDate);
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
        await fixture.Collector.TickAsync(true);
        CollectionJob previous = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Complete, previous.Status);
        fixture.Library.Files.Remove(("NFLX", new(2026, 9, 4)));
        fixture.Clock.Now = DateTimeOffset.Parse("2026-09-08T20:15:00Z");

        await fixture.Collector.TickAsync(true);

        Assert.Equal(new[] { "NFLX:2026-09-04", "NFLX:2026-09-04", "NFLX:2026-09-08" },
            fixture.Provider.Requests.Select(Key));
        Assert.Equal(previous.Id, fixture.Collector.State.Jobs.Single(j => j.SessionDate == previous.SessionDate).Id);
        Assert.All(fixture.Collector.State.Jobs, j => Assert.Equal(CollectionJobStatus.Complete, j.Status));
        Assert.Empty(fixture.Collector.State.ContinuityGaps);
    }

    private static string Key(HistoricalDataRequest request) => $"{request.Symbol}:{request.FromUtc:yyyy-MM-dd}";
    private static CollectionSessionWindow Window(string day) => CollectionSchedule.GetSessionWindow(DateOnly.Parse(day));

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
            Library.Save(Download(new(symbol, Window(day).FromUtc, Window(day).ThroughUtc), omitLast));
        public void Restart() => Collector = CreateCollector();
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
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (Result == CollectionJobStatus.Failed) throw new InvalidDataException("Provider data is temporarily invalid.");
            HistoricalDownload download = Download(request, Result == CollectionJobStatus.Partial ? 1 : 0);
            if (Result == CollectionJobStatus.Unavailable) download = download with { Candles = [] };
            return Task.FromResult(download);
        }
    }

    private sealed class Library : IMarketDataLibrary
    {
        public string RootPath => Path.GetFullPath("scheduled-continuity-test-library");
        public Dictionary<(string Symbol, DateOnly Date), HistoricalDataset> Files { get; } = [];
        public MarketDataLibraryScan Scan() => new(Files.Values.Select(Describe).ToArray(), []);
        public HistoricalDataset Read(string hash) => Files.Values.Single(d => d.DatasetHash == hash);

        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
        {
            var day = DateOnly.FromDateTime(download.RequestedFromUtc.UtcDateTime);
            var dataset = new HistoricalDataset(1, "America/New_York", Guid.NewGuid().ToString("N"), download.Provider,
                download.InstrumentId, download.Symbol, day, download.SourceIntervalSeconds,
                download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds, download.FetchedAtUtc,
                Coverage(download.RequestedFromUtc, download.RequestedThroughUtc, download.SourceIntervalSeconds, download.Candles),
                download.Candles);
            Files[(download.Symbol, day)] = dataset;
            return [Describe(dataset)];
        }

        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            HistoricalDataset[] datasets = Files.Values.Where(d => d.Symbol == query.Symbol &&
                d.SourceIntervalSeconds == query.SourceIntervalSeconds &&
                d.Coverage.RequestedFromUtc < query.ThroughUtc && d.Coverage.RequestedThroughUtc > query.FromUtc).ToArray();
            HistoricalCandle[] candles = datasets.SelectMany(d => d.Candles)
                .Where(c => c.StartsAtUtc >= query.FromUtc && c.EndsAtUtc <= query.ThroughUtc).OrderBy(c => c.StartsAtUtc).ToArray();
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
            var starts = candles.Select(c => c.StartsAtUtc).ToHashSet();
            var gaps = new List<HistoricalGap>();
            DateTimeOffset? gapStart = null;
            for (DateTimeOffset time = from; time < through; time = time.AddSeconds(interval))
            {
                if (!starts.Contains(time)) gapStart ??= time;
                else if (gapStart is { } start) { gaps.Add(new(start, time)); gapStart = null; }
            }
            if (gapStart is { } lastGap) gaps.Add(new(lastGap, through));
            return new(from, through, candles.Count == 0 ? null : candles.Min(c => c.StartsAtUtc),
                candles.Count == 0 ? null : candles.Max(c => c.EndsAtUtc), expected, candles.Count,
                gaps.Count == 0, candles.All(c => c.Volume is not null), gaps);
        }
    }
}
