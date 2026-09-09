using System.Globalization;
using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class AllHoursCollectionTests
{
    private static readonly DateOnly Day = new(2026, 9, 8);
    private static readonly DateTimeOffset DayStart = At("2026-09-08T04:00:00Z");
    private static readonly DateTimeOffset DayEnd = At("2026-09-09T04:00:00Z");

    [Theory]
    [InlineData("2026-09-08", "2026-09-08T04:00:00Z", "2026-09-09T04:00:00Z", 5760)]
    [InlineData("2026-09-04", "2026-09-04T04:00:00Z", "2026-09-05T00:00:00Z", 4800)]
    [InlineData("2026-09-13", "2026-09-14T00:00:00Z", "2026-09-14T04:00:00Z", 960)]
    [InlineData("2026-09-07", "2026-09-08T00:00:00Z", "2026-09-08T04:00:00Z", 960)]
    [InlineData("2026-11-26", "2026-11-27T01:00:00Z", "2026-11-27T05:00:00Z", 960)]
    [InlineData("2026-11-27", "2026-11-27T05:00:00Z", "2026-11-27T22:00:00Z", 4080)]
    public async Task FinalizedDateCollectsActualTradingWindowsInBoundedChunks(
        string date, string from, string through, int expectedCandles)
    {
        DateOnly day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTimeOffset start = At(from), end = At(through);
        using var fixture = new Fixture(end.AddHours(1));
        await fixture.Collector.QueueManualAsync(["SOFI"], day, day, sessionBounds: "24_5");
        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.NotEmpty(fixture.Provider.Requests);
        DateTimeOffset next = start;
        foreach (HistoricalDataRequest request in fixture.Provider.Requests)
        {
            Assert.Equal(next, request.FromUtc);
            Assert.True(request.ThroughUtc <= end);
            AssertBoundedRequest(request);
            next = request.ThroughUtc;
        }
        Assert.Equal(end, next);
        HistoricalDataQueryResult saved = fixture.Read(start, end);
        Assert.True(saved.Succeeded);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(expectedCandles, saved.Candles.Count);
        HistoricalDatasetInfo daily = Assert.Single(saved.Datasets);
        Assert.Equal(day, daily.TradingDate);
        Assert.Equal("24_5", daily.SessionBounds);
        Assert.EndsWith(".15s.json", daily.RelativePath);
        Assert.Equal(daily.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.All(saved.Candles, candle => Assert.True(candle.EndsAtUtc <= end));
    }

    [Theory]
    [InlineData("2026-09-05")]
    [InlineData("2026-09-06")]
    public async Task ClosedWeekendAndSundayBeforeMondayHolidayDoNotCreateJobs(string date)
    {
        using var fixture = new Fixture();
        DateOnly day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        await fixture.Collector.QueueManualAsync(["SOFI"], day, day, sessionBounds: "24_5");
        await fixture.Collector.TickAsync(true);
        Assert.Empty(fixture.Collector.State.Jobs);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Library.Scan().Datasets);
    }

    [Theory]
    [InlineData("regular", "2026-09-08T13:30:00Z", "2026-09-08T20:00:00Z")]
    [InlineData("extended", "2026-09-08T08:00:00Z", "2026-09-09T00:00:00Z")]
    public async Task ExistingSessionFileIsReusedWithoutRequestsInsideItsSavedCoverage(
        string bounds, string from, string through)
    {
        using var fixture = new Fixture();
        DateTimeOffset cachedFrom = At(from), cachedThrough = At(through);
        HistoricalDatasetInfo original = fixture.Save(cachedFrom, cachedThrough, bounds);
        byte[] originalBytes = fixture.Bytes(original);
        await fixture.QueueDay();
        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.All(fixture.Provider.Requests, request =>
            Assert.True(request.ThroughUtc <= cachedFrom || request.FromUtc >= cachedThrough,
                $"Requested cached interval: {request.FromUtc:O} through {request.ThroughUtc:O}."));
        HistoricalDataQueryResult saved = fixture.Read(DayStart, DayEnd);
        Assert.True(saved.Succeeded);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(5760, saved.Candles.Count);
        HistoricalDatasetInfo daily = Assert.Single(saved.Datasets);
        Assert.Equal("24_5", daily.SessionBounds);
        Assert.Equal(original.RelativePath, daily.RelativePath);
        Assert.NotEqual(original.DatasetHash, daily.DatasetHash);
        Assert.Equal(daily.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        fixture.AssertArchived(original, originalBytes);
    }

    [Fact]
    public async Task TwoInteriorHolesRequestOnlyTheirExactMissingCandles()
    {
        using var fixture = new Fixture();
        var holes = new[]
        {
            new HistoricalGap(DayStart.AddHours(1).AddSeconds(45), DayStart.AddHours(1).AddSeconds(90)),
            new HistoricalGap(DayStart.AddHours(17).AddMinutes(30), DayStart.AddHours(17).AddMinutes(30).AddSeconds(30)),
        };
        HistoricalCandle[] candles = Candles(DayStart, DayEnd).Where(candle =>
            !holes.Any(hole => candle.StartsAtUtc >= hole.FromUtc && candle.EndsAtUtc <= hole.ThroughUtc)).ToArray();
        HistoricalDatasetInfo original = fixture.Save(DayStart, DayEnd, "24_5", candles);
        byte[] originalBytes = fixture.Bytes(original);
        await fixture.QueueDay();
        await fixture.Drain();

        Assert.Equal(holes.Select(hole => (hole.FromUtc, hole.ThroughUtc)),
            fixture.Provider.Requests.Select(request => (request.FromUtc, request.ThroughUtc)));
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        HistoricalDataQueryResult saved = fixture.Read(DayStart, DayEnd);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(5760, saved.Candles.Count);
        Assert.Equal(Assert.Single(saved.Datasets).DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        fixture.AssertArchived(original, originalBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailableAndScheduledCollectionCaptureOnlyCompletedCandlesDespiteLegacySettings(bool automatic)
    {
        DateTimeOffset now = At("2026-09-08T17:12:37Z");
        DateTimeOffset cutoff = At("2026-09-08T17:12:30Z");
        using var fixture = new Fixture(now, automatic);
        fixture.Provider.OldestAvailable = DayStart;
        Assert.Equal("regular", fixture.Collector.State.Settings.SessionBounds);
        if (automatic) await fixture.Collector.TickAsync(true);
        else await fixture.Collector.QueueAvailableAsync();
        CollectionJob captured = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Day);
        Assert.Equal(cutoff, captured.RequestedThroughUtc);
        Assert.Equal(automatic, captured.IsAutomatic);

        fixture.Clock.Now = now.AddSeconds(30);
        await fixture.Drain();

        Assert.All(fixture.Collector.State.Jobs, job => Assert.Equal("24_5", job.SessionBounds));
        CollectionJob current = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Day);
        Assert.Equal(CollectionJobStatus.Complete, current.Status);
        Assert.Equal(cutoff, current.RequestedThroughUtc);
        HistoricalDataRequest[] today = fixture.Provider.Requests.Where(request => request.FromUtc >= DayStart).ToArray();
        Assert.NotEmpty(today);
        Assert.All(today, request =>
        {
            AssertBoundedRequest(request);
            Assert.True(request.ThroughUtc <= cutoff);
        });
        HistoricalDataQueryResult saved = fixture.Read(DayStart, cutoff);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal("24_5", Assert.Single(saved.Datasets).SessionBounds);
        Assert.Equal(cutoff, saved.Candles[^1].EndsAtUtc);
        Assert.All(saved.Candles, candle => Assert.True(candle.AvailableAtUtc <= cutoff));
        Assert.Empty(fixture.Read(cutoff, cutoff.AddSeconds(15)).Candles);
        if (automatic) Assert.Equal(At("2026-09-08T17:00:00Z"), fixture.Store.Load().LastScheduledOccurrenceUtc);
    }

    [Fact]
    public async Task RestartResumesSavedCursorAndCompletedRequeueDoesNotDownloadAgain()
    {
        using var fixture = new Fixture();
        await fixture.QueueDay();
        await fixture.Collector.TickAsync(true);
        HistoricalDataRequest first = Assert.Single(fixture.Provider.Requests);
        CollectionJob persisted = Assert.Single(fixture.Store.Load().Jobs);
        Assert.Equal(CollectionJobStatus.Pending, persisted.Status);
        Assert.Equal(first.ThroughUtc, persisted.NextGapFromUtc);
        Assert.True(persisted.ReceivedCandlesThisRun);
        HistoricalDatasetInfo original = Assert.Single(fixture.Library.Scan().Datasets);
        byte[] originalBytes = fixture.Bytes(original);

        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        Assert.Equal(first.ThroughUtc, fixture.Provider.Requests[1].FromUtc);
        await fixture.Drain();
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        fixture.AssertArchived(original, originalBytes);
        HistoricalDataQueryResult saved = fixture.Read(DayStart, DayEnd);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(Assert.Single(saved.Datasets).DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        string[] hashes = fixture.Library.Scan().Datasets.Select(dataset => dataset.DatasetHash).Order().ToArray();

        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        await fixture.QueueDay();
        await fixture.Drain();
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.Equal(persisted.Id, Assert.Single(fixture.Collector.State.Jobs).Id);
        Assert.Equal(hashes, fixture.Library.Scan().Datasets.Select(dataset => dataset.DatasetHash).Order());
    }

    [Fact]
    public async Task EmptyChunkCursorSurvivesRestartAndLaterDataIsSavedWithoutAnEndlessLoop()
    {
        using var fixture = new Fixture();
        fixture.Provider.EmptyBefore = DayStart.AddHours(6);
        await fixture.QueueDay();
        await fixture.Collector.TickAsync(true);
        CollectionJob persisted = Assert.Single(fixture.Store.Load().Jobs);
        Assert.Equal(DayStart.AddHours(6), persisted.NextGapFromUtc);
        Assert.False(persisted.ReceivedCandlesThisRun);
        Assert.Empty(fixture.Library.Scan().Datasets);

        fixture.Restart();
        await fixture.Drain();
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.Equal(DayStart.AddHours(6), fixture.Provider.Requests[1].FromUtc);
        CollectionJob completed = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Partial, completed.Status);
        Assert.Equal(15, completed.ActualSourceIntervalSeconds);
        HistoricalDataQueryResult saved = fixture.Read(DayStart, DayEnd);
        Assert.False(saved.Coverage.Complete);
        Assert.Equal(4320, saved.Candles.Count);
        Assert.Equal(Assert.Single(saved.Datasets).DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.Equal(DayEnd, saved.Candles[^1].EndsAtUtc);
        Assert.Equal(new HistoricalGap(DayStart, DayStart.AddHours(6)), Assert.Single(saved.Coverage.Gaps));
        for (int i = 0; i < 3; i++) Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Equal(4, fixture.Provider.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAndManualRequeueResetCursorAndRequestOnlyThePreviouslyEmptyChunk(bool requeue)
    {
        using var fixture = new Fixture();
        fixture.Provider.EmptyBefore = DayStart.AddHours(6);
        await fixture.QueueDay();
        await fixture.Drain();
        CollectionJob partial = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Partial, partial.Status);
        Assert.Equal(DayEnd, partial.NextGapFromUtc);
        HistoricalDatasetInfo original = Assert.Single(fixture.Library.Scan().Datasets);
        byte[] originalBytes = fixture.Bytes(original);
        fixture.Provider.EmptyBefore = null;
        fixture.Provider.Requests.Clear();

        if (requeue) await fixture.QueueDay();
        else await fixture.Collector.RetryMissingAsync([partial.Id]);
        CollectionJob queued = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(partial.Id, queued.Id);
        Assert.Null(queued.NextGapFromUtc);
        Assert.False(queued.ReceivedCandlesThisRun);
        await fixture.Drain();

        HistoricalDataRequest request = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(DayStart, request.FromUtc);
        Assert.Equal(DayStart.AddHours(6), request.ThroughUtc);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        HistoricalDataQueryResult saved = fixture.Read(DayStart, DayEnd);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(Assert.Single(saved.Datasets).DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        fixture.AssertArchived(original, originalBytes);
    }

    [Theory]
    [InlineData("instrument")]
    [InlineData("provider")]
    [InlineData("policy")]
    [InlineData("basis")]
    [InlineData("interval")]
    [InlineData("session")]
    [InlineData("symbol")]
    [InlineData("requested-range")]
    [InlineData("invalid-prices")]
    [InlineData("duplicate-candle")]
    [InlineData("forming-candle")]
    [InlineData("cached-overlap")]
    public async Task InvalidProviderDataAndProvenanceFailWithoutChangingSavedFiles(string invalid)
    {
        using var fixture = new Fixture();
        HistoricalDatasetInfo original = fixture.Save(At("2026-09-08T13:30:00Z"), At("2026-09-08T20:00:00Z"), "regular");
        byte[] originalBytes = fixture.Bytes(original);
        fixture.Provider.Transform = response => invalid switch
        {
            "instrument" => response with { InstrumentId = "other-SOFI" },
            "provider" => response with { Provider = "other-provider" },
            "policy" => response with { AdjustmentPolicy = "unadjusted" },
            "basis" => response with { AdjustmentBasis = "other-basis" },
            "interval" => response with { SourceIntervalSeconds = 30 },
            "session" => response with { SessionBounds = "regular" },
            "symbol" => response with { Symbol = "NVDA" },
            "requested-range" => response with { RequestedFromUtc = response.RequestedFromUtc.AddSeconds(15) },
            "invalid-prices" => response with { Candles = [response.Candles[0] with { High = 8m }] },
            "duplicate-candle" => response with { Candles = [response.Candles[0], response.Candles[0]] },
            "forming-candle" => response with
            {
                Candles = [new(response.RequestedThroughUtc, response.RequestedThroughUtc.AddSeconds(15),
                    response.RequestedThroughUtc.AddSeconds(15), 10m, 11m, 9m, 10m, 100m)],
            },
            "cached-overlap" => response with { Candles = [fixture.Library.Read(original.DatasetHash).Candles[0] with { Close = 10.5m }] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        await fixture.QueueDay();
        await fixture.Drain();

        CollectionJob job = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Failed, job.Status);
        Assert.False(string.IsNullOrWhiteSpace(job.Error));
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(original.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.Equal(originalBytes, fixture.Bytes(original));
    }

    private static void AssertBoundedRequest(HistoricalDataRequest request)
    {
        Assert.Equal("24_5", request.SessionBounds);
        Assert.Equal(15, request.SourceIntervalSeconds);
        Assert.Equal("SOFI-id", request.InstrumentId);
        Assert.InRange((request.ThroughUtc - request.FromUtc).TotalSeconds / 15, 1d, 1440d);
        Assert.Equal(0, request.FromUtc.UtcTicks % (15 * TimeSpan.TicksPerSecond));
        Assert.Equal(0, request.ThroughUtc.UtcTicks % (15 * TimeSpan.TicksPerSecond));
    }

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
    private static HistoricalCandle[] Candles(DateTimeOffset from, DateTimeOffset through) =>
        Enumerable.Range(0, (int)((through - from).TotalSeconds / 15)).Select(index =>
        {
            DateTimeOffset at = from.AddSeconds(index * 15);
            return new HistoricalCandle(at, at.AddSeconds(15), at.AddSeconds(15), 10m, 11m, 9m, 10m, 100m);
        }).ToArray();

    private sealed class Fixture : IDisposable
    {
        public Fixture(DateTimeOffset? now = null, bool automatic = false)
        {
            Clock.Now = now ?? DayEnd.AddHours(1);
            Library = new(Path.Combine(Path.GetTempPath(), "PriceSentinel-all-hours-tests", Guid.NewGuid().ToString("N")));
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath, CatchUpCalendarDays = 1,
                AutomaticDownloadsEnabled = automatic, AutomaticEnabledAtUtc = automatic ? Clock.Now.AddHours(-1) : null,
                DailyDownloadTime = new(17, 0), TimeZoneId = "UTC",
                Lists = [new(Guid.NewGuid(), "Included", true, [new("SOFI", ProviderInstrumentId: "SOFI-id")])],
            } });
            Provider = new(Clock);
            Restart();
        }
        public Clock Clock { get; } = new();
        public MemoryStore Store { get; } = new();
        public Provider Provider { get; }
        public JsonMarketDataLibrary Library { get; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public void Restart() => Collector = new(Store, Provider, root => new JsonMarketDataLibrary(root), Clock,
            new() { MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero, MaximumRequestsPerTick = 1 });
        public Task QueueDay() => Collector.QueueManualAsync(["SOFI"], Day, Day, sessionBounds: "24_5");
        public HistoricalDataQueryResult Read(DateTimeOffset from, DateTimeOffset through) => Library.Query(
            new("SOFI", from, through, SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage,
                IncludeCompatibleSessions: true));
        public HistoricalDatasetInfo Save(DateTimeOffset from, DateTimeOffset through, string bounds,
            IReadOnlyList<HistoricalCandle>? candles = null) => Assert.Single(Library.Save(
                new("test", "SOFI-id", "SOFI", 15, "split", "robinhood-split-unversioned", bounds,
                    Clock.Now, from, through, candles ?? Candles(from, through))));
        public byte[] Bytes(HistoricalDatasetInfo dataset) => File.ReadAllBytes(Path.Combine(Library.RootPath, dataset.RelativePath));
        public void AssertArchived(HistoricalDatasetInfo original, byte[] originalBytes)
        {
            string archivePath = Path.Combine(Library.RootPath, ".archive", original.DatasetHash + ".json");
            Assert.Equal(originalBytes, File.ReadAllBytes(archivePath));
            HistoricalDataset archived = Library.Read(original.DatasetHash);
            Assert.Equal(original.DatasetHash, archived.DatasetHash);
            Assert.Equal(original.SessionBounds, archived.SessionBounds);
            Assert.Equal(original.Coverage.ActualCandleCount, archived.Candles.Count);
            HistoricalDataQueryResult pinned = Library.Query(new("SOFI", original.Coverage.RequestedFromUtc,
                original.Coverage.RequestedThroughUtc, SessionBounds: original.SessionBounds, PinnedHashes: [original.DatasetHash]));
            Assert.True(pinned.Succeeded);
            Assert.Equal(original.DatasetHash, Assert.Single(pinned.Datasets).DatasetHash);
            Assert.Equal(archived.Candles, pinned.Candles);
        }
        public async Task Drain()
        {
            for (int i = 0; i < 100 && Collector.State.Jobs.Any(job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading); i++)
                await Collector.TickAsync(true);
            Assert.DoesNotContain(Collector.State.Jobs, job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading);
        }
        public void Dispose()
        {
            if (Directory.Exists(Library.RootPath)) Directory.Delete(Library.RootPath, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
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
        public DateTimeOffset? OldestAvailable { get; set; }
        public DateTimeOffset? EmptyBefore { get; set; }
        public Func<HistoricalDownload, HistoricalDownload>? Transform { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            IReadOnlyList<HistoricalCandle> candles = request.FromUtc < OldestAvailable || request.FromUtc < EmptyBefore
                ? [] : Candles(request.FromUtc, request.ThroughUtc);
            var response = new HistoricalDownload("test", "SOFI-id", request.Symbol, 15, request.AdjustmentPolicy,
                "robinhood-split-unversioned", request.SessionBounds, clock.Now, request.FromUtc, request.ThroughUtc, candles);
            return Task.FromResult(Transform?.Invoke(response) ?? response);
        }
    }
}
