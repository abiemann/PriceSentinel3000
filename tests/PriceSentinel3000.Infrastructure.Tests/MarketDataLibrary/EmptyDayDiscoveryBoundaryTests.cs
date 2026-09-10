using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class EmptyDayDiscoveryBoundaryTests
{
    private static readonly DateOnly Today = new(2026, 9, 9);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task FullyEmptyOlderDayStopsAfterOnePass_AndNewRunRemembersBoundaryInDailyJson()
    {
        using var fixture = new Fixture();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] older = fixture.Provider.Requests.Where(request => Date(request) == Yesterday).ToArray();
        Assert.NotEmpty(older);
        Assert.All(older.GroupBy(request => (request.FromUtc, request.ThroughUtc)), group => Assert.Single(group));
        Assert.DoesNotContain(fixture.Provider.Requests, request => Date(request) < Yesterday);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
        CollectionGapSnapshot remembered = new JsonCollectionGapIndex(fixture.Library.RootPath).Query(
            fixture.Key(Yesterday), day.FromUtc, day.ThroughUtc, fixture.Clock.Now);
        Assert.NotEmpty(remembered.AttemptedRanges);
        Assert.All(remembered.AttemptedRanges, attempt => Assert.Equal(1, attempt.Attempts));
        Assert.Equal(day.ThroughUtc - day.FromUtc,
            TimeSpan.FromTicks(remembered.UnavailableRanges.Sum(gap => (gap.ThroughUtc - gap.FromUtc).Ticks)));

        int requests = fixture.Provider.Requests.Count;
        fixture.Restart();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(Today, Assert.Single(fixture.Collector.State.Jobs).SessionDate);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }

    [Fact]
    public async Task ForcedRunRechecksRememberedEmptyBoundaryOnce_ThenStopsAgain()
    {
        using var fixture = new Fixture();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        int requests = fixture.Provider.Requests.Count;
        int initialBoundaryRequests = fixture.Provider.Requests.Count(request => Date(request) == Yesterday);
        fixture.Restart();

        await fixture.Collector.QueueForcedAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] forced = fixture.Provider.Requests.Skip(requests).ToArray();
        Assert.Equal(initialBoundaryRequests, forced.Length);
        Assert.All(forced, request => Assert.Equal(Yesterday, Date(request)));
        Assert.All(forced.GroupBy(request => (request.FromUtc, request.ThroughUtc)), group => Assert.Single(group));
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }

    [Fact]
    public async Task EmptyBrokerPassStopsEvenWithSavedCandles_AndDailyJsonRemembersBoundary()
    {
        using var fixture = new Fixture();
        fixture.SaveFirstCandle(Yesterday);

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] partial = fixture.Provider.Requests.Where(request => Date(request) == Yesterday).ToArray();
        Assert.NotEmpty(partial);
        Assert.All(partial.GroupBy(request => (request.FromUtc, request.ThroughUtc)), group => Assert.Single(group));
        Assert.DoesNotContain(fixture.Provider.Requests, request => Date(request) < Yesterday);
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
        Assert.NotNull(new JsonCollectionGapIndex(fixture.Library.RootPath).Query(
            fixture.Key(Yesterday), day.FromUtc, day.ThroughUtc, fixture.Clock.Now).BrokerHistoryUnavailableAtUtc);
        HistoricalDataset saved = fixture.Library.Read(Assert.Single(fixture.Library.Scan().Datasets,
            item => item.TradingDate == Yesterday).DatasetHash);
        Assert.Single(saved.Candles);
        Assert.Equal(CollectionJobStatus.Partial,
            Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday).Status);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
        int requests = fixture.Provider.Requests.Count;
        fixture.Restart();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(Today, Assert.Single(fixture.Collector.State.Jobs).SessionDate);
    }

    [Fact]
    public async Task ResumeOfPreviouslyQueuedSecondEmptyPassStopsWithoutAnotherBrokerRequest()
    {
        using var fixture = new Fixture();
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
        await fixture.Collector.QueueAvailableAsync();
        for (int tick = 0; tick < 150; tick++)
        {
            await fixture.Collector.TickAsync(true);
            if (fixture.Provider.Requests.LastOrDefault() is { } last &&
                Date(last) == Yesterday && last.ThroughUtc == day.ThroughUtc) break;
        }
        CollectionState state = fixture.Collector.State;
        CollectionJob boundary = Assert.Single(state.Jobs, job => job.SessionDate == Yesterday);
        Assert.Equal(CollectionJobStatus.Pending, boundary.Status);
        Assert.Equal(day.ThroughUtc, boundary.NextGapFromUtc);
        // Simulate the second full pass already queued by the preceding app version.
        fixture.Store.Save(state with
        {
            Jobs = state.Jobs.Select(job => job.Id == boundary.Id
                ? job with { NextGapFromUtc = null, AvailabilityCheckPending = true } : job).ToArray(),
        });
        int requests = fixture.Provider.Requests.Count;
        fixture.Restart();

        await fixture.Drain();

        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(CollectionJobStatus.Unavailable,
            Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday).Status);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedOldRunRecoversPartialDayBoundaryBeforeAnyOlderBrokerRequest(bool startFresh)
    {
        using var fixture = new Fixture();
        fixture.SaveFirstCandle(Yesterday);
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
        await fixture.Collector.QueueAvailableAsync();
        for (int tick = 0; tick < 150; tick++)
        {
            await fixture.Collector.TickAsync(true);
            if (fixture.Provider.Requests.LastOrDefault() is { } last &&
                Date(last) == Yesterday && last.ThroughUtc == day.ThroughUtc) break;
        }
        CollectionState state = fixture.Collector.State;
        CollectionJob checkedDay = Assert.Single(state.Jobs, job => job.SessionDate == Yesterday);
        CollectionJob older = checkedDay with { Id = Guid.NewGuid(), SessionDate = Yesterday.AddDays(-1),
            Status = CollectionJobStatus.Pending, NextGapFromUtc = null, LastAttemptAtUtc = null, SavedCoveragePercent = null };
        CollectionJob manual = older with { Id = Guid.NewGuid(), IsAvailabilityProbe = false,
            AvailabilityRunId = null, Status = CollectionJobStatus.Failed, Error = "Unrelated explicit work" };
        fixture.Store.Save(state with
        {
            Jobs = state.Jobs.Select(job => job.Id == checkedDay.Id
                ? job with { Status = CollectionJobStatus.Partial } : job).Concat([older, manual]).ToArray(),
            AvailabilityRun = state.AvailabilityRun! with { CurrentDate = older.SessionDate, CurrentJobIds = [older.Id] },
        });
        int requests = fixture.Provider.Requests.Count;
        fixture.Restart();
        if (startFresh) await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Null(fixture.Collector.State.AvailabilityRun);
        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.Id == older.Id);
        CollectionJob retainedManual = Assert.Single(fixture.Collector.State.Jobs, job => job.Id == manual.Id);
        Assert.Equal(manual.Status, retainedManual.Status);
        Assert.Equal(manual.Error, retainedManual.Error);
        Assert.NotNull(new JsonCollectionGapIndex(fixture.Library.RootPath).Query(
            fixture.Key(Yesterday), day.FromUtc, day.ThroughUtc, fixture.Clock.Now).BrokerHistoryUnavailableAtUtc);
    }

    [Fact]
    public async Task FreshQueueInSameCollectorPreservesEmptyPassEvidenceCompletedAfterInitialReconciliation()
    {
        using var fixture = new Fixture("AAPL", "MSFT");
        fixture.SaveFirstCandle(Yesterday);
        fixture.Provider.FailedOlderSymbols.Add("MSFT");
        await fixture.Collector.QueueAvailableAsync();
        // This first tick initializes reconciliation while no older work has finished.
        await fixture.Collector.TickAsync(true);
        CollectionAvailabilityRun originalRun = fixture.Collector.State.AvailabilityRun!;
        Assert.NotNull(originalRun);
        for (int tick = 0; tick < 150; tick++)
        {
            await fixture.Collector.TickAsync(true);
            if (fixture.Collector.State.Jobs.Any(job => job.Symbol == "AAPL" && job.SessionDate == Yesterday &&
                job.Status == CollectionJobStatus.Partial)) break;
        }
        CollectionState before = fixture.Collector.State;
        CollectionJob checkedDay = Assert.Single(before.Jobs, job => job.Symbol == "AAPL" && job.SessionDate == Yesterday);
        Assert.Equal(CollectionJobStatus.Partial, checkedDay.Status);
        Assert.False(checkedDay.ReceivedCandlesThisRun);
        Assert.NotNull(checkedDay.LastAttemptAtUtc);
        Assert.Equal(CollectionJobStatus.Failed,
            Assert.Single(before.Jobs, job => job.Symbol == "MSFT" && job.SessionDate == Yesterday).Status);
        Assert.Equal(originalRun.Id, before.AvailabilityRun!.Id);
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
        var index = new JsonCollectionGapIndex(fixture.Library.RootPath);
        // MSFT's failure held the date barrier, so AAPL's terminal row is still the
        // only evidence of an empty provider pass. No boundary has been persisted.
        Assert.Null(index.Query(fixture.Key(Yesterday), day.FromUtc, day.ThroughUtc, fixture.Clock.Now).BrokerHistoryUnavailableAtUtc);
        int appleRequests = fixture.Provider.Requests.Count(request => request.Symbol == "AAPL");

        await fixture.Collector.QueueAvailableAsync();

        Assert.NotEqual(originalRun.Id, fixture.Collector.State.AvailabilityRun!.Id);
        Assert.NotNull(index.Query(fixture.Key(Yesterday), day.FromUtc, day.ThroughUtc, fixture.Clock.Now).BrokerHistoryUnavailableAtUtc);
        await fixture.Drain();
        Assert.Equal(appleRequests, fixture.Provider.Requests.Count(request => request.Symbol == "AAPL"));
        Assert.DoesNotContain(fixture.Collector.State.Jobs, job => job.Symbol == "AAPL" && job.SessionDate < Today);
    }

    [Fact]
    public async Task CandlesReturnedOnFirstPassKeepDayAvailableAcrossAnEmptyGapRetry()
    {
        using var fixture = new Fixture();
        fixture.Provider.AdditionalCandles = [Candle(CollectionSchedule.GetSessionWindow(Yesterday, "24_5").FromUtc)];
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        CollectionJob productive = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday);
        Assert.True(productive.ReceivedCandlesThisRun);
        Assert.Contains(fixture.Provider.Requests, request => Date(request) < Yesterday);
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(Yesterday, "24_5");
        Assert.Null(new JsonCollectionGapIndex(fixture.Library.RootPath).Query(
            fixture.Key(Yesterday), day.FromUtc, day.ThroughUtc, fixture.Clock.Now).BrokerHistoryUnavailableAtUtc);
    }

    private static DateOnly Date(HistoricalDataRequest request) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, Eastern).DateTime);

    private sealed class Fixture : IDisposable
    {
        public Fixture(params string[] symbols)
        {
            if (symbols.Length == 0) symbols = ["AAPL"];
            Library = new(Path.Combine(Path.GetTempPath(), "PriceSentinel-empty-boundary-" + Guid.NewGuid().ToString("N")));
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath,
                Lists = [new(Guid.NewGuid(), "Included", true,
                    symbols.Select(symbol => new DownloadListMember(symbol, ProviderInstrumentId: symbol + "-id")).ToArray())],
            } });
            Provider = new(Clock);
            Restart();
        }

        public JsonMarketDataLibrary Library { get; }
        public MemoryStore Store { get; } = new();
        public Clock Clock { get; } = new();
        public Provider Provider { get; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public CollectionGapKey Key(DateOnly day) => new("AAPL", "AAPL-id", day,
            "24_5", "split", "robinhood-split-unversioned");

        public void Restart() => Collector = new(Store, Provider, _ => Library, Clock, new()
        {
            MaximumRequestsPerTick = 2, MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero,
        }, root => new JsonCollectionGapIndex(root));

        public void SaveFirstCandle(DateOnly date)
        {
            DateTimeOffset at = CollectionSchedule.GetSessionWindow(date, "24_5").FromUtc;
            Library.Save(new("Robinhood", "AAPL-id", "AAPL", 15, "split", "robinhood-split-unversioned", "24_5",
                Clock.Now, at, at.AddSeconds(15), [Candle(at)]));
        }

        public async Task Drain()
        {
            for (int tick = 0; tick < 150; tick++)
                if (await Collector.TickAsync(true) == CollectionBatchResult.Idle) return;
            throw new InvalidOperationException("The empty-day boundary did not stop discovery.");
        }

        public void Dispose()
        {
            string root = Path.GetFullPath(Library.RootPath);
            string temporary = Path.GetFullPath(Path.GetTempPath());
            if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("PriceSentinel-empty-boundary-", StringComparison.Ordinal))
                throw new InvalidOperationException("The test library is outside its temporary directory.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static HistoricalCandle Candle(DateTimeOffset at) =>
        new(at, at.AddSeconds(15), at.AddSeconds(15), 10, 11, 9, 10, 100);

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
        public HistoricalCandle[] AdditionalCandles { get; set; } = [];
        public HashSet<string> FailedOlderSymbols { get; } = [];

        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Date(request) < Today && FailedOlderSymbols.Contains(request.Symbol))
                throw new InvalidDataException("Provider failure holds the current date barrier.");
            HistoricalCandle[] candles = Date(request) >= Today
                ? Enumerable.Range(0, (int)((request.ThroughUtc - request.FromUtc).TotalSeconds / 15))
                    .Select(index => Candle(request.FromUtc.AddSeconds(index * 15))).ToArray()
                : AdditionalCandles.Where(candle => candle.StartsAtUtc >= request.FromUtc && candle.EndsAtUtc <= request.ThroughUtc).ToArray();
            return Task.FromResult(new HistoricalDownload("Robinhood", request.Symbol + "-id", request.Symbol, 15,
                request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, clock.Now,
                request.FromUtc, request.ThroughUtc, candles));
        }
    }
}
