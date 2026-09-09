using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class ProgressiveGapDiscoveryTests
{
    private static readonly DateOnly Today = new(2026, 9, 16);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task NewDownloadQueuesOnlyTodayAndReplacesSupersededDiscoveryWithoutRequeueingOldFailures()
    {
        using var fixture = new Fixture("SOFI", "NVDA");
        CollectionJob superseded = fixture.JobFor("SOFI", Yesterday, CollectionJobStatus.Pending) with { IsAvailabilityProbe = true };
        CollectionJob failure = fixture.JobFor("SOFI", Today.AddDays(-20), CollectionJobStatus.Failed);
        CollectionJob manual = fixture.JobFor("SOFI", Today.AddDays(-21), CollectionJobStatus.Pending);
        fixture.Store.Save(fixture.Store.Load() with { Jobs = [superseded, failure, manual] });
        fixture.Restart();

        await fixture.Collector.QueueAvailableAsync();

        CollectionJob[] current = fixture.Collector.State.Jobs.Where(job => job.DiscoveryAsOfDate == Today).ToArray();
        Assert.Equal(new[] { "NVDA", "SOFI" }, current.Select(job => job.Symbol).Order());
        Assert.All(current, job =>
        {
            Assert.Equal(Today, job.SessionDate);
            Assert.Equal(CollectionJobStatus.Pending, job.Status);
            Assert.Equal(fixture.Clock.Now, job.RequestedThroughUtc);
            Assert.True(job.IsAvailabilityProbe);
            Assert.False(job.AvailabilityCheckPending);
        });
        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.Id == superseded.Id);
        AssertUnchangedJob(failure, Assert.Single(fixture.Collector.State.Jobs, job => job.Id == failure.Id));
        AssertUnchangedJob(manual, Assert.Single(fixture.Collector.State.Jobs, job => job.Id == manual.Id));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(3, fixture.Collector.State.Jobs.Count(job => job.Status == CollectionJobStatus.Pending));
    }

    [Fact]
    public async Task EachStockStopsAtItsOwnFirstEmptyRegularDayAndNeverStartsASecondRound()
    {
        using var fixture = new Fixture("SOFI", "NVDA");
        fixture.Provider.Add("SOFI", Candle(Open(Yesterday).AddHours(9.5)));
        fixture.Provider.Add("NVDA", Candle(Open(Yesterday).AddHours(9.5)), Candle(Open(Yesterday.AddDays(-1)).AddHours(9.5)));

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(new DateOnly(2026, 9, 14), fixture.Provider.Requests.Where(request => request.Symbol == "SOFI").Min(Day));
        // Sunday has overnight trading but no regular session, so its empty response is not a retention boundary.
        Assert.Equal(new DateOnly(2026, 9, 11), fixture.Provider.Requests.Where(request => request.Symbol == "NVDA").Min(Day));
        Assert.Contains(fixture.Provider.Requests, request => request.Symbol == "NVDA" && Day(request) == new DateOnly(2026, 9, 13));
        Assert.DoesNotContain(fixture.Provider.Requests, request => request.Symbol is "EXCLUDED" or "DISABLED");
        Assert.All(fixture.Provider.Requests, request => Assert.Equal(15, request.SourceIntervalSeconds));
        Assert.Equal(fixture.Provider.Requests.Count,
            fixture.Provider.Requests.Select(request => (request.Symbol, request.FromUtc, request.ThroughUtc)).Distinct().Count());
        Assert.All(fixture.Collector.State.Jobs.GroupBy(job => (job.Symbol, job.SessionDate)), group => Assert.Single(group));
        int count = fixture.Provider.Requests.Count;
        fixture.Restart();
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Equal(count, fixture.Provider.Requests.Count);
    }

    [Fact]
    public async Task EmptyEarlyHoursDoNotHideLaterDataAndPositiveProbeCandlesAreSavedImmediately()
    {
        using var fixture = new Fixture();
        HistoricalCandle middleOfHour = Candle(Open(Yesterday).AddHours(9).AddMinutes(37).AddSeconds(15), 12m);
        fixture.Provider.Add("SOFI", middleOfHour);
        await fixture.Collector.QueueAvailableAsync();

        await fixture.Until(() => fixture.Provider.Requests.Any(request => Day(request) == Yesterday && Contains(request, middleOfHour)));

        HistoricalDataRequest[] probes = fixture.Provider.Requests.Where(request => Day(request) == Yesterday).ToArray();
        Assert.True(probes.Length > 1);
        Assert.All(probes, request => Assert.InRange(request.ThroughUtc - request.FromUtc, TimeSpan.FromSeconds(15), TimeSpan.FromHours(1)));
        Assert.Equal(Open(Yesterday), probes[0].FromUtc);
        for (int index = 1; index < probes.Length; index++) Assert.Equal(probes[index - 1].ThroughUtc, probes[index].FromUtc);
        Assert.All(probes[..^1], request => Assert.False(Contains(request, middleOfHour)));
        Assert.Equal(middleOfHour, Assert.Single(fixture.Read("SOFI", Yesterday).Candles));
        CollectionJob active = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday);
        Assert.True(active.ReceivedCandlesThisRun);
        Assert.False(active.AvailabilityCheckPending);

        await fixture.Drain();

        Assert.Equal(middleOfHour, Assert.Single(fixture.Read("SOFI", Yesterday).Candles));
        Assert.Single(fixture.Library.Scan().Datasets, dataset => dataset.Symbol == "SOFI" && dataset.TradingDate == Yesterday);
        Assert.Contains(fixture.Provider.Requests, request => Day(request) < Yesterday);
        Assert.All(fixture.Provider.Requests, request => Assert.True(request.ThroughUtc - request.FromUtc <= TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task DisjointMissingRangesMergeIntoOneDayWithoutChangingExistingCandles()
    {
        using var fixture = new Fixture();
        DateTimeOffset open = Open(Yesterday);
        HistoricalCandle[] original = [Candle(open.AddSeconds(30), 11m), Candle(open.AddHours(2), 12m), Candle(open.AddHours(13), 13m)];
        HistoricalDatasetInfo saved = fixture.Seed("SOFI", Yesterday, original);
        HistoricalCandle[] additions = [Candle(open.AddSeconds(15), 14m), Candle(open.AddHours(1), 15m), Candle(open.AddHours(7), 16m)];
        fixture.Provider.Add("SOFI", additions);

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        HistoricalDataQueryResult result = fixture.Read("SOFI", Yesterday);
        Assert.Equal(original.Concat(additions).OrderBy(candle => candle.StartsAtUtc), result.Candles);
        Assert.Equal(original, fixture.Library.Read(saved.DatasetHash).Candles);
        Assert.False(result.Coverage.Complete);
        Assert.NotEmpty(result.Coverage.Gaps);
        Assert.Single(fixture.Library.Scan().Datasets, dataset => dataset.Symbol == "SOFI" && dataset.TradingDate == Yesterday);
        Assert.DoesNotContain(fixture.Provider.Requests.Where(request => Day(request) == Yesterday), request =>
            original.Any(candle => request.FromUtc == candle.StartsAtUtc && request.ThroughUtc == candle.EndsAtUtc));
    }

    [Fact]
    public async Task RestartContinuesAtPersistedProbePositionWithoutRepeatingCompletedWindows()
    {
        using var fixture = new Fixture();
        HistoricalCandle later = Candle(Open(Yesterday).AddHours(9.5));
        fixture.Provider.Add("SOFI", later);
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Until(() => fixture.Provider.Requests.Count(request => Day(request) == Yesterday) == 3);
        HistoricalDataRequest last = fixture.Provider.Requests.Last();
        CollectionJob active = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday);
        Assert.Equal(last.ThroughUtc, active.NextGapFromUtc);
        Assert.True(active.AvailabilityCheckPending);
        Assert.False(active.ReceivedCandlesThisRun);
        int previousRequests = fixture.Provider.Requests.Count;

        fixture.Restart();
        await fixture.Drain();

        Assert.All(fixture.Provider.Requests.Skip(previousRequests).Where(request => Day(request) == Yesterday),
            request => Assert.True(request.FromUtc >= last.ThroughUtc));
        Assert.Equal(fixture.Provider.Requests.Count,
            fixture.Provider.Requests.Select(request => (request.Symbol, request.FromUtc, request.ThroughUtc)).Distinct().Count());
        Assert.Equal(later, Assert.Single(fixture.Read("SOFI", Yesterday).Candles));
    }

    [Fact]
    public async Task EmptyOlderDayChecksEveryGapThenStopsEvenWhenThatDayAlreadyHasSavedCandles()
    {
        using var fixture = new Fixture();
        HistoricalCandle savedCandle = Candle(Open(Yesterday).AddHours(12), 21m);
        fixture.Seed("SOFI", Yesterday, [savedCandle]);
        HistoricalCoverage originalCoverage = fixture.Read("SOFI", Yesterday).Coverage;
        CollectionJob oldFailure = fixture.JobFor("SOFI", Today.AddDays(-20), CollectionJobStatus.Failed);
        fixture.Store.Save(fixture.Store.Load() with { Jobs = [oldFailure] });
        fixture.Restart();
        fixture.Provider.Add("SOFI", Candle(Open(oldFailure.SessionDate).AddHours(9.5)));

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] requests = fixture.Provider.Requests.Where(request => Day(request) == Yesterday).ToArray();
        AssertCoversGaps(originalCoverage.Gaps, requests);
        Assert.All(requests, request => Assert.True(request.ThroughUtc - request.FromUtc <= TimeSpan.FromHours(1)));
        Assert.DoesNotContain(fixture.Provider.Requests, request => Day(request) < Yesterday);
        Assert.Equal(savedCandle, Assert.Single(fixture.Read("SOFI", Yesterday).Candles));
        Assert.Equal(CollectionJobStatus.Partial, Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday).Status);
        AssertUnchangedJob(oldFailure, Assert.Single(fixture.Collector.State.Jobs, job => job.Id == oldFailure.Id));
    }

    [Fact]
    public async Task CompletelyEmptyOlderRegularDayEndsTheChainAfterItsWholeTradingWindow()
    {
        using var fixture = new Fixture();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] requests = fixture.Provider.Requests.Where(request => Day(request) == Yesterday).ToArray();
        AssertCoversGaps(CollectionSchedule.GetSessionWindows(Yesterday, "24_5")
            .Select(window => new HistoricalGap(window.FromUtc, window.ThroughUtc)).ToArray(), requests);
        Assert.All(requests, request => Assert.True(request.ThroughUtc - request.FromUtc <= TimeSpan.FromHours(1)));
        Assert.DoesNotContain(fixture.Provider.Requests, request => Day(request) < Yesterday);
        Assert.Equal(CollectionJobStatus.Unavailable, Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday).Status);
        Assert.DoesNotContain(fixture.Library.Scan().Datasets, dataset => dataset.TradingDate == Yesterday);
    }

    [Fact]
    public async Task CompleteSavedDaySkipsTheBrokerButContinuesCheckingEarlierDays()
    {
        using var fixture = new Fixture();
        fixture.Queries.CompleteDays.Add(("SOFI", Yesterday));
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.DoesNotContain(fixture.Provider.Requests, request => Day(request) == Yesterday);
        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Yesterday.AddDays(-1));
        Assert.DoesNotContain(fixture.Provider.Requests, request => Day(request) < Yesterday.AddDays(-1));
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday).Status);
    }

    [Theory]
    [InlineData("2026-09-16", "2026-09-15")]
    [InlineData("2026-09-14", "2026-09-11")]
    [InlineData("2026-09-08", "2026-09-04")]
    public async Task EmptyTodayAndEmptyOvernightOnlyDatesDoNotEstablishTheBrokerBoundary(string todayText, string boundaryText)
    {
        DateOnly today = DateOnly.Parse(todayText), boundary = DateOnly.Parse(boundaryText);
        using var fixture = new Fixture(today: today);
        fixture.Provider.Candles.Clear();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Contains(fixture.Provider.Requests, request => Day(request) == today);
        Assert.Equal(boundary, fixture.Provider.Requests.Min(Day));
        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientAndConnectionFailuresKeepDiscoveryPendingAndRetryTheSameWindow(bool connectionFailure)
    {
        using var fixture = new Fixture();
        HistoricalCandle later = Candle(Open(Yesterday).AddHours(9.5));
        fixture.Provider.Add("SOFI", later);
        fixture.Provider.FailOnce = request => Day(request) == Yesterday
            ? connectionFailure ? new MarketDataConnectionUnavailableException("Connection interrupted.") : new HttpRequestException("Temporary broker failure.")
            : null;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Until(() => fixture.Provider.FailureRaised);
        HistoricalDataRequest failedRequest = fixture.Provider.Requests.Last();
        CollectionJob pending = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday);
        Assert.Equal(CollectionJobStatus.Pending, pending.Status);
        Assert.Equal(Today, pending.DiscoveryAsOfDate);
        Assert.True(pending.AvailabilityCheckPending);
        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.SessionDate < Yesterday);
        if (connectionFailure) Assert.Equal(CollectionBatchResult.Disconnected, fixture.LastBatch);

        fixture.Restart();
        await fixture.Drain();

        Assert.Equal(2, fixture.Provider.Requests.Count(request => request == failedRequest));
        Assert.Equal(later, Assert.Single(fixture.Read("SOFI", Yesterday).Candles));
        Assert.Contains(fixture.Provider.Requests, request => Day(request) < Yesterday);
    }

    private static void AssertUnchangedJob(CollectionJob expected, CollectionJob actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.QueuedAtUtc, actual.QueuedAtUtc);
        Assert.Equal(expected.LastAttemptAtUtc, actual.LastAttemptAtUtc);
        Assert.Equal(expected.Error, actual.Error);
        Assert.Equal(expected.DatasetHashes, actual.DatasetHashes);
    }

    private static HistoricalCandle Candle(DateTimeOffset start, decimal price = 10m) =>
        new(start, start.AddSeconds(15), start.AddSeconds(15), price, price + 1, price - 1, price, 100);

    private static DateTimeOffset Open(DateOnly day) => CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc;
    private static DateOnly Day(HistoricalDataRequest request) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, Eastern).DateTime);
    private static bool Contains(HistoricalDataRequest request, HistoricalCandle candle) =>
        request.FromUtc <= candle.StartsAtUtc && request.ThroughUtc >= candle.EndsAtUtc;

    private static void AssertCoversGaps(IReadOnlyList<HistoricalGap> gaps, IReadOnlyList<HistoricalDataRequest> requests)
    {
        Assert.NotEmpty(requests);
        foreach (HistoricalGap gap in gaps)
        {
            DateTimeOffset coveredThrough = gap.FromUtc;
            foreach (HistoricalDataRequest request in requests.OrderBy(request => request.FromUtc))
            {
                if (request.ThroughUtc <= coveredThrough) continue;
                if (request.FromUtc > coveredThrough) break;
                coveredThrough = request.ThroughUtc;
                if (coveredThrough >= gap.ThroughUtc) break;
            }
            Assert.True(coveredThrough >= gap.ThroughUtc, $"Missing range {gap.FromUtc:O}–{gap.ThroughUtc:O} was not completely checked.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        public Fixture(params string[] symbols) : this(Today, symbols) { }
        public Fixture(DateOnly today, params string[] symbols)
        {
            if (symbols.Length == 0) symbols = ["SOFI"];
            Clock = new(Open(today).AddSeconds(15));
            Library = new(Path.Combine(_temporaryRoot, "PriceSentinel-progressive-gaps-" + Guid.NewGuid().ToString("N")));
            Queries = new(Library);
            Provider = new(Clock);
            foreach (string symbol in symbols) Provider.Add(symbol, Candle(Open(today)));
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath, CatchUpCalendarDays = 30,
                Lists = [new(Guid.NewGuid(), "Included", true,
                    [.. symbols.Select(symbol => new DownloadListMember(symbol)), new("EXCLUDED", IsIncluded: false)]),
                    new(Guid.NewGuid(), "Duplicate", true, [new(symbols[0])]),
                    new(Guid.NewGuid(), "Disabled", false, [new("DISABLED")])],
            } });
            Restart();
        }
        public Clock Clock { get; }
        public MemoryStore Store { get; } = new();
        public Provider Provider { get; }
        public JsonMarketDataLibrary Library { get; }
        public QueryLibrary Queries { get; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public CollectionBatchResult LastBatch { get; private set; }
        public CollectionJob JobFor(string symbol, DateOnly day, CollectionJobStatus status) => new()
        {
            Symbol = symbol, SessionDate = day, Status = status, SessionBounds = "24_5", LibraryRootPath = Library.RootPath,
            QueuedAtUtc = Clock.Now.AddDays(-1), Error = "Previous work.",
        };
        public void Restart() => Collector = new(Store, Provider, _ => Queries, Clock, new()
        {
            MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero,
        });
        public HistoricalDatasetInfo Seed(string symbol, DateOnly day, HistoricalCandle[] candles)
        {
            CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(day, "24_5");
            return Assert.Single(Library.Save(new("test", symbol + "-id", symbol, 15, "split", "robinhood-split-unversioned",
                "24_5", Clock.Now, window.FromUtc, window.ThroughUtc, candles)));
        }
        public HistoricalDataQueryResult Read(string symbol, DateOnly day)
        {
            CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(day, "24_5");
            return Library.Query(new(symbol, window.FromUtc, window.ThroughUtc, 15, SessionBounds: "24_5",
                RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage, IncludeCompatibleSessions: true));
        }
        public async Task Until(Func<bool> completed)
        {
            for (int index = 0; index < 300 && !completed(); index++) LastBatch = await Collector.TickAsync(true);
            Assert.True(completed(), "Collection did not reach the expected step within 300 ticks.");
        }
        public async Task Drain()
        {
            await Until(() => !Collector.State.Jobs.Any(job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading));
            Assert.DoesNotContain(Collector.State.Jobs, job => job.Status == CollectionJobStatus.Failed && job.Error != "Previous work.");
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(Library.RootPath);
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("PriceSentinel-progressive-gaps-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the test's temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : ICollectionStateStore
    {
        private string _json = JsonSerializer.Serialize(new CollectionState());
        public CollectionState Load() => JsonSerializer.Deserialize<CollectionState>(_json)!;
        public void Save(CollectionState state) => _json = JsonSerializer.Serialize(state);
    }

    private sealed class Provider(Clock clock) : IMarketHistoryProvider
    {
        public List<HistoricalDataRequest> Requests { get; } = [];
        public Dictionary<string, List<HistoricalCandle>> Candles { get; } = [];
        public Func<HistoricalDataRequest, Exception?>? FailOnce { get; set; }
        public bool FailureRaised { get; private set; }
        public void Add(string symbol, params HistoricalCandle[] candles)
        {
            if (!Candles.TryGetValue(symbol, out List<HistoricalCandle>? current)) Candles[symbol] = current = [];
            current.AddRange(candles);
        }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (!FailureRaised && FailOnce?.Invoke(request) is { } exception)
            {
                FailureRaised = true;
                throw exception;
            }
            HistoricalCandle[] candles = Candles.GetValueOrDefault(request.Symbol, [])
                .Where(candle => Contains(request, candle)).OrderBy(candle => candle.StartsAtUtc).ToArray();
            return Task.FromResult(new HistoricalDownload("test", request.Symbol + "-id", request.Symbol, 15,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, clock.Now,
                request.FromUtc, request.ThroughUtc, candles));
        }
    }

    private sealed class QueryLibrary(JsonMarketDataLibrary inner) : IMarketDataLibrary
    {
        public HashSet<(string Symbol, DateOnly Day)> CompleteDays { get; } = [];
        public string RootPath => inner.RootPath;
        public MarketDataLibraryScan Scan() => inner.Scan();
        public HistoricalDataset Read(string hash) => inner.Read(hash);
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => inner.Save(download);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            HistoricalDataQueryResult actual = inner.Query(query);
            DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(query.FromUtc, Eastern).DateTime);
            if (!CompleteDays.Contains((query.Symbol, day))) return actual;
            // Completion is a trusted query input here; storage coverage validation has its own tests.
            return actual with { Coverage = actual.Coverage with { Complete = true, Gaps = [] } };
        }
    }
}
