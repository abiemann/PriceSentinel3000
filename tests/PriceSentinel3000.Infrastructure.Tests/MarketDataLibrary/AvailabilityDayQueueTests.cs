using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class AvailabilityDayQueueTests
{
    private static readonly DateOnly Today = new(2026, 9, 9);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task TodayAlwaysQueues_ButCompleteAndCappedOlderDaysNeverAddRows()
    {
        using var fixture = new Fixture("AAPL", "MSFT");
        foreach (string symbol in fixture.Symbols) fixture.Save(symbol, Today, 1);
        fixture.Save("AAPL", Yesterday);
        fixture.Save("MSFT", Yesterday, 1);
        fixture.Index.Seed(fixture.Key("MSFT", Yesterday), 2, unavailable: true);
        foreach (string symbol in fixture.Symbols)
            foreach (DateOnly date in new[] { Yesterday.AddDays(-1), new DateOnly(2026, 9, 4) })
                fixture.Index.Seed(fixture.Key(symbol, date), 2, unavailable: true);

        await fixture.Collector.QueueAvailableAsync();
        Assert.Equal(2, fixture.Collector.State.Jobs.Count);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.Equal(Today, job.SessionDate));
        await fixture.Drain();

        Assert.Equal(2, fixture.Collector.State.Jobs.Count);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }

    [Fact]
    public async Task AllTickersFinishOneDateBeforeOlderDateStarts_EmptyTickerStopsIndependently()
    {
        using var fixture = new Fixture("AAPL", "MSFT");
        fixture.Provider.Oldest["AAPL"] = Today;
        fixture.Provider.Oldest["MSFT"] = Yesterday;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(Yesterday, fixture.Provider.Requests.Where(request => request.Symbol == "AAPL").Min(Date));
        Assert.Equal(new DateOnly(2026, 9, 4), fixture.Provider.Requests.Where(request => request.Symbol == "MSFT").Min(Date));
        DateOnly[] dates = fixture.Provider.Requests.Select(Date).ToArray();
        Assert.Equal(dates.OrderDescending(), dates);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }

    [Fact]
    public async Task OlderEmptyDayGetsOnePass_ThenNoOlderRowsOrRepeatRequests()
    {
        using var fixture = new Fixture("AAPL");
        fixture.Provider.Oldest["AAPL"] = Today;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] older = fixture.Provider.Requests.Where(request => Date(request) == Yesterday).ToArray();
        Assert.NotEmpty(older);
        Assert.All(older.GroupBy(request => (request.FromUtc, request.ThroughUtc)), group => Assert.Single(group));
        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.SessionDate < Yesterday);
        int requests = fixture.Provider.Requests.Count;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(Today, Assert.Single(fixture.Collector.State.Jobs).SessionDate);
    }

    [Fact]
    public async Task ForcedRunRetriesOlderDayEvenWhenItsUnavailableAttemptsAreAlreadyCapped()
    {
        using var fixture = new Fixture("AAPL");
        fixture.Save("AAPL", Today, 1);
        fixture.Index.Seed(fixture.Key("AAPL", Yesterday), 2, unavailable: true);
        await fixture.Collector.QueueForcedAvailableAsync();
        await fixture.Drain();

        Assert.Contains(fixture.Provider.Requests, request => Date(request) == Yesterday);
        Assert.DoesNotContain(fixture.Provider.Requests, request => Date(request) < Yesterday);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }

    [Fact]
    public async Task RestartRetainsDayBarrierAndMembers_AndDoesNotRepeatFinishedRequests()
    {
        using var fixture = new Fixture("AAPL", "MSFT");
        foreach (string symbol in fixture.Symbols) fixture.Provider.Oldest[symbol] = Today;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Collector.TickAsync(true);
        CollectionAvailabilityRun run = Assert.IsType<CollectionAvailabilityRun>(fixture.Collector.State.AvailabilityRun);
        Guid[] jobIds = run.CurrentJobIds.ToArray();
        fixture.Restart();
        Assert.Equal(run.Id, fixture.Collector.State.AvailabilityRun!.Id);
        Assert.Equal(jobIds, fixture.Collector.State.AvailabilityRun.CurrentJobIds);
        await fixture.Drain();

        Assert.All(fixture.Provider.Requests.Where(request => Date(request) == Today)
            .GroupBy(request => (request.Symbol, request.FromUtc, request.ThroughUtc)), group => Assert.Single(group));
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }

    [Fact]
    public async Task FreshRunRemovesScopedOldAvailabilityRows_AndPreservesExplicitOrUnrelatedWork()
    {
        using var fixture = new Fixture("AAPL");
        CollectionJob old = new()
        {
            Symbol = "AAPL", SessionDate = Yesterday, SessionBounds = "24_5", IsAvailabilityProbe = true,
            Status = CollectionJobStatus.Partial, LibraryRootPath = fixture.Library.RootPath,
        };
        CollectionJob explicitJob = old with { Id = Guid.NewGuid(), IsAvailabilityProbe = false, Status = CollectionJobStatus.Failed };
        CollectionJob unrelated = old with { Id = Guid.NewGuid(), Symbol = "OTHER" };
        fixture.Store.Save(fixture.Store.Load() with { Jobs = [old, explicitJob, unrelated] });
        fixture.Restart();

        await fixture.Collector.QueueAvailableAsync();

        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.Id == old.Id);
        Assert.Contains(fixture.Collector.State.Jobs, job => job == explicitJob);
        Assert.Contains(fixture.Collector.State.Jobs, job => job == unrelated);
        Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Today);
    }

    [Fact]
    public async Task FailedCurrentDayPausesFrontierWithoutClassifyingOlderHistoryUnavailable()
    {
        using var fixture = new Fixture("AAPL");
        fixture.Provider.Fails = true;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Failed, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Equal(Today, fixture.Collector.State.AvailabilityRun!.CurrentDate);
        Assert.All(fixture.Provider.Requests, request => Assert.Equal(Today, Date(request)));
        fixture.Restart();
        int requests = fixture.Provider.Requests.Count;
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Equal(requests, fixture.Provider.Requests.Count);
    }

    [Fact]
    public async Task OlderPrequeueChecksPublishCurrentTickerWhileFinishedRowsRemainVisible_ThenClearActivity()
    {
        using var fixture = new Fixture("AAPL", "MSFT");
        foreach (string symbol in fixture.Symbols)
        {
            fixture.Save(symbol, Today, 1);
            fixture.Index.Seed(fixture.Key(symbol, Yesterday), 2, unavailable: true);
        }
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        fixture.CollectionLibrary = new ObservedLibrary(fixture.Library, query =>
        {
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(query.FromUtc, Eastern).DateTime) != Yesterday) return;
            TaskCompletionSource entered = query.Symbol == "AAPL" ? firstEntered : secondEntered;
            ManualResetEventSlim release = query.Symbol == "AAPL" ? releaseFirst : releaseSecond;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The held older-history query was not released.");
        });
        fixture.Restart();
        var published = new System.Collections.Concurrent.ConcurrentQueue<CollectionActivity>();
        fixture.Collector.StateChanged += (_, _) =>
        {
            if (fixture.Collector.Activity is { Stage: "CheckingOlderHistory" } activity) published.Enqueue(activity);
        };
        await fixture.Collector.QueueAvailableAsync();
        Task<CollectionBatchResult> tick = fixture.Collector.TickAsync(true);
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertPlanning("AAPL");
            releaseFirst.Set();
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertPlanning("MSFT");
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await tick.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(CollectionBatchResult.Idle, await tick);
        Assert.False(fixture.Collector.IsBusy);
        Assert.Null(fixture.Collector.Activity);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
        Assert.Empty(fixture.Provider.Requests);

        void AssertPlanning(string symbol)
        {
            Assert.True(fixture.Collector.IsBusy);
            CollectionActivity activity = Assert.IsType<CollectionActivity>(fixture.Collector.Activity);
            Assert.Equal("CheckingOlderHistory", activity.Stage);
            Assert.Equal(symbol, activity.Symbol);
            Assert.Equal(Yesterday, activity.SessionDate);
            CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
            Assert.Equal(day.FromUtc, activity.FromUtc);
            Assert.Equal(day.ThroughUtc, activity.ThroughUtc);
            Assert.Contains(published, item => item.Stage == activity.Stage && item.Symbol == symbol && item.SessionDate == Yesterday);
            Assert.Equal(2, fixture.Collector.State.Jobs.Count);
            Assert.All(fixture.Collector.State.Jobs, job =>
            {
                Assert.Equal(Today, job.SessionDate);
                Assert.Equal(CollectionJobStatus.Complete, job.Status);
            });
        }
    }

    [Fact]
    public async Task PauseDuringOlderPrecheckStopsBeforeTheNextTickerAndDoesNotQueueMoreDates()
    {
        using var fixture = new Fixture("AAPL", "MSFT");
        using var cancellation = new CancellationTokenSource();
        foreach (string symbol in fixture.Symbols) fixture.Save(symbol, Today, 1);
        var checkedSymbols = new List<string>();
        fixture.CollectionLibrary = new ObservedLibrary(fixture.Library, query =>
        {
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(query.FromUtc, Eastern).DateTime) != Yesterday) return;
            checkedSymbols.Add(query.Symbol);
            cancellation.Cancel();
        });
        fixture.Restart();
        await fixture.Collector.QueueAvailableAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector.TickAsync(true, cancellation.Token));

        Assert.Equal(new[] { "AAPL" }, checkedSymbols);
        Assert.Empty(fixture.Provider.Requests);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.Equal(Today, job.SessionDate));
        Assert.False(fixture.Collector.IsBusy);
        Assert.Null(fixture.Collector.Activity);
    }

    private static DateOnly Date(HistoricalDataRequest request) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, Eastern).DateTime);

    private sealed class Fixture : IDisposable
    {
        public Fixture(params string[] symbols)
        {
            Symbols = symbols;
            Library = new(Path.Combine(Path.GetTempPath(), "PriceSentinel-day-queue-" + Guid.NewGuid().ToString("N")));
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath,
                Lists = [new(Guid.NewGuid(), "Included", true, symbols.Select(symbol => new DownloadListMember(symbol,
                    ProviderInstrumentId: symbol + "-id")).ToArray())],
            } });
            Provider = new(Clock);
            Restart();
        }
        public string[] Symbols { get; }
        public JsonMarketDataLibrary Library { get; }
        public IMarketDataLibrary? CollectionLibrary { get; set; }
        public MemoryStore Store { get; } = new();
        public Clock Clock { get; } = new();
        public AttemptIndex Index { get; } = new();
        public Provider Provider { get; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public CollectionGapKey Key(string symbol, DateOnly day) => new(symbol, symbol + "-id", day,
            "24_5", "split", "robinhood-split-unversioned");
        public void Restart() => Collector = new(Store, Provider, _ => CollectionLibrary ?? Library, Clock, new()
        {
            MaximumRequestsPerTick = 2, MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero,
        }, _ => Index);
        public void Save(string symbol, DateOnly day, int? count = null)
        {
            CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(day, "24_5");
            DateTimeOffset through = day == Today ? Clock.Now : window.ThroughUtc;
            int candles = count ?? (int)((through - window.FromUtc).TotalSeconds / 15);
            Library.Save(new("test", symbol + "-id", symbol, 15, "split", "robinhood-split-unversioned", "24_5",
                Clock.Now, window.FromUtc, through, Enumerable.Range(0, candles).Select(index =>
                {
                    DateTimeOffset at = window.FromUtc.AddSeconds(index * 15);
                    return new HistoricalCandle(at, at.AddSeconds(15), at.AddSeconds(15), 10, 11, 9, 10, 100);
                }).ToArray()));
        }
        public async Task Drain()
        {
            for (int tick = 0; tick < 500; tick++)
                if (await Collector.TickAsync(true) == CollectionBatchResult.Idle) return;
            throw new InvalidOperationException("Availability queue did not finish.");
        }
        public void Dispose()
        {
            if (Directory.Exists(Library.RootPath)) Directory.Delete(Library.RootPath, true);
        }
    }

    private sealed class ObservedLibrary(IMarketDataLibrary inner, Action<HistoricalDataQuery> beforeQuery) : IMarketDataLibrary
    {
        public string RootPath => inner.RootPath;
        public MarketDataLibraryScan Scan() => inner.Scan();
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => inner.Save(download);
        public HistoricalDataset Read(string datasetHash) => inner.Read(datasetHash);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            beforeQuery(query);
            return inner.Query(query);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-09-09T04:00:15Z");
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
        public Dictionary<string, DateOnly> Oldest { get; } = [];
        public bool Fails { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Fails) throw new HttpRequestException("Temporary failure.");
            HistoricalCandle[] candles = Date(request) >= Oldest.GetValueOrDefault(request.Symbol, Today)
                ? Enumerable.Range(0, (int)((request.ThroughUtc - request.FromUtc).TotalSeconds / 15)).Select(index =>
                {
                    DateTimeOffset at = request.FromUtc.AddSeconds(index * 15);
                    return new HistoricalCandle(at, at.AddSeconds(15), at.AddSeconds(15), 10, 11, 9, 10, 100);
                }).ToArray() : [];
            return Task.FromResult(new HistoricalDownload("test", request.Symbol + "-id", request.Symbol, 15,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, clock.Now,
                request.FromUtc, request.ThroughUtc, candles));
        }
    }

    private sealed class AttemptIndex : ICollectionGapIndex
    {
        private readonly Dictionary<(CollectionGapKey Key, long At), int> _attempts = [];
        private readonly HashSet<(CollectionGapKey Key, long At)> _unavailable = [];
        private readonly HashSet<CollectionGapKey> _received = [];
        public bool SupportsAttemptTracking => true;
        public void Initialize() { }
        public void Seed(CollectionGapKey key, int attempts, bool unavailable)
        {
            foreach (CollectionSessionWindow window in CollectionSchedule.GetSessionWindows(key.SessionDate, key.SessionBounds))
                foreach (long at in Slots(window.FromUtc, window.ThroughUtc))
                {
                    _attempts[(key, at)] = attempts;
                    if (unavailable) _unavailable.Add((key, at));
                }
        }
        public CollectionGapSnapshot Query(CollectionGapKey key, DateTimeOffset from, DateTimeOffset through, DateTimeOffset now) =>
            new(Slots(from, through).Where(at => _unavailable.Contains((key, at))).Select(Gap).ToArray(), _received.Contains(key))
            {
                AttemptedRanges = Slots(from, through).Where(at => _attempts.ContainsKey((key, at)))
                    .Select(at => new CollectionGapAttempt(Gap(at), _attempts[(key, at)])).ToArray(),
            };
        public void RecordDownloadAttempt(CollectionGapKey key, DateTimeOffset from, DateTimeOffset through, DateTimeOffset checkedAt)
        {
            foreach (long at in Slots(from, through)) _attempts[(key, at)] = Math.Min(2, _attempts.GetValueOrDefault((key, at)) + 1);
        }
        public void ResolveSavedRanges(CollectionGapKey key, IReadOnlyList<HistoricalGap> savedRanges)
        {
            foreach (HistoricalGap gap in savedRanges)
                foreach (long at in Slots(gap.FromUtc, gap.ThroughUtc))
                {
                    _attempts.Remove((key, at));
                    _unavailable.Remove((key, at));
                }
        }
        public void RecordAttempt(CollectionGapKey key, DateTimeOffset from, DateTimeOffset through,
            IReadOnlyList<HistoricalGap> unavailable, bool receivedCandles, DateTimeOffset checkedAt, DateTimeOffset? retryAfter)
        {
            foreach (long at in Slots(from, through)) _unavailable.Remove((key, at));
            foreach (HistoricalGap gap in unavailable)
                foreach (long at in Slots(gap.FromUtc, gap.ThroughUtc)) _unavailable.Add((key, at));
            if (receivedCandles) _received.Add(key);
        }
        private static HistoricalGap Gap(long at) => new(new DateTimeOffset(at, TimeSpan.Zero),
            new DateTimeOffset(at, TimeSpan.Zero).AddSeconds(15));
        private static IEnumerable<long> Slots(DateTimeOffset from, DateTimeOffset through)
        {
            for (long at = from.UtcTicks; at < through.UtcTicks; at += 15 * TimeSpan.TicksPerSecond) yield return at;
        }
    }
}
