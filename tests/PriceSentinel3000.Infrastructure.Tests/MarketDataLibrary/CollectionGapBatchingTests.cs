using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class CollectionGapBatchingTests
{
    [Fact]
    public async Task HundredsOfSparseHolesUseFewQueriesAndMergeExactCandlesPreservingArchivedSnapshot()
    {
        using var fixture = new Fixture();
        HistoricalDatasetInfo original = fixture.Seed(index => index % 9 != 4);
        byte[] originalBytes = fixture.Bytes(original);
        Assert.Equal(640, original.Coverage.Gaps.Count);

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.InRange(fixture.Provider.Requests.Count, 1, 6);
        Assert.InRange(fixture.CountedLibrary.QueryCalls, 1, fixture.Provider.Requests.Count + 2);
        Assert.All(fixture.Provider.Requests, request =>
        {
            Assert.Equal("24_5", request.SessionBounds);
            Assert.Equal(15, request.SourceIntervalSeconds);
            Assert.InRange(request.ThroughUtc - request.FromUtc, TimeSpan.FromSeconds(15), TimeSpan.FromHours(6));
        });
        HistoricalDataQueryResult saved = fixture.Read();
        Assert.True(saved.Succeeded);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(5760, saved.Candles.Count);
        Assert.Equal(fixture.AllCandles, saved.Candles);
        Assert.Equal(saved.Candles.Count, saved.Candles.Select(candle => candle.StartsAtUtc).Distinct().Count());
        Assert.Equal(originalBytes, fixture.Bytes(original with { RelativePath = Path.Combine(".archive", original.DatasetHash + ".json") }));
        Assert.Equal(fixture.Provider.Requests.Count, fixture.CountedLibrary.SaveCalls);
    }

    [Fact]
    public async Task FiveMinuteSavedSpanCanBeBridgedButAnIsolatedHoleKeepsExactEdges()
    {
        using var fixture = new Fixture();
        fixture.Seed(index => index is not (1 or 22 or 44));

        await fixture.Drain();

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(fixture.From.AddSeconds(15), fixture.Provider.Requests[0].FromUtc);
        Assert.Equal(fixture.From.AddSeconds(345), fixture.Provider.Requests[0].ThroughUtc);
        Assert.Equal(fixture.From.AddSeconds(660), fixture.Provider.Requests[1].FromUtc);
        Assert.Equal(fixture.From.AddSeconds(675), fixture.Provider.Requests[1].ThroughUtc);
        Assert.True(fixture.Read().Coverage.Complete);
    }

    [Fact]
    public async Task CurrentDayBatchingHonorsTheCompletedCandleCutoff()
    {
        using var fixture = new Fixture(cutoffAfter: TimeSpan.FromHours(13) + TimeSpan.FromMinutes(17) + TimeSpan.FromSeconds(15));
        fixture.Seed(index => index % 9 != 4);

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.Equal(fixture.Through, fixture.Job.RequestedThroughUtc);
        Assert.InRange(fixture.Provider.Requests.Count, 1, 4);
        Assert.All(fixture.Provider.Requests, request => Assert.True(request.ThroughUtc <= fixture.Through));
        Assert.True(fixture.Read().Coverage.Complete);
        Assert.Equal(fixture.AllCandles, fixture.Read().Candles);
        Assert.DoesNotContain(fixture.Library.Scan().Datasets.SelectMany(dataset => fixture.Library.Read(dataset.DatasetHash).Candles),
            candle => candle.EndsAtUtc > fixture.Through);
    }

    [Theory]
    [InlineData("2026-09-13")]
    [InlineData("2026-11-27")]
    public async Task GapsInClosedHoursDoNotExpandSundayOrEarlyCloseRequests(string date)
    {
        using var fixture = new Fixture(DateOnly.Parse(date));
        fixture.Seed(index => index % 9 != 4, includeClosedHoursInRequestedCoverage: true);

        await fixture.Drain();

        IReadOnlyList<CollectionSessionWindow> windows = CollectionSchedule.GetSessionWindows(fixture.Day, "24_5");
        Assert.All(fixture.Provider.Requests, request => Assert.Contains(windows,
            window => request.FromUtc >= window.FromUtc && request.ThroughUtc <= window.ThroughUtc));
        Assert.InRange(fixture.Provider.Requests.Count, 1, 4);
        Assert.Equal(CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.Equal(fixture.AllCandles, fixture.Read().Candles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrAlreadySavedResponsesFinishWithoutNewFilesOrRepeatedRegions(bool alreadySavedOnly)
    {
        using var fixture = new Fixture();
        HistoricalDatasetInfo original = fixture.Seed(index => index % 9 != 4);
        byte[] bytes = fixture.Bytes(original);
        var existingStarts = fixture.Library.Read(original.DatasetHash).Candles.Select(candle => candle.StartsAtUtc).ToHashSet();
        fixture.Provider.Transform = candles => alreadySavedOnly
            ? candles.Where(candle => existingStarts.Contains(candle.StartsAtUtc)).ToArray() : [];

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Partial, fixture.Job.Status);
        Assert.InRange(fixture.Provider.Requests.Count, 1, 6);
        Assert.Equal(0, fixture.CountedLibrary.SaveCalls);
        Assert.Equal(original.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.Equal(bytes, fixture.Bytes(original));
        Assert.Equal(original.DatasetHash, Assert.Single(fixture.Job.DatasetHashes));
        Assert.Equal(640, fixture.Read().Coverage.Gaps.Count);
        Assert.InRange(fixture.CountedLibrary.QueryCalls, 1, fixture.Provider.Requests.Count + 2);
        int requests = fixture.Provider.Requests.Count;
        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(fixture.Provider.Requests.Count, fixture.Provider.Requests.Distinct().Count());
    }

    [Theory]
    [InlineData("end")]
    [InlineData("available")]
    public async Task ConflictingTimingIsRejectedBeforeWritingAnyRevision(string field)
    {
        using var fixture = new Fixture();
        HistoricalDatasetInfo original = fixture.Seed(index => index is not (1 or 10));
        byte[] bytes = fixture.Bytes(original);
        DateTimeOffset overlap = fixture.From.AddSeconds(30);
        fixture.Provider.Transform = candles => candles.Select(candle => candle.StartsAtUtc != overlap ? candle : field switch
        {
            "open" => candle with { Open = 10.25m },
            "volume" => candle with { Volume = 101 },
            "end" => candle with { EndsAtUtc = candle.EndsAtUtc.AddSeconds(15) },
            "available" => candle with { AvailableAtUtc = candle.AvailableAtUtc.AddSeconds(15) },
            _ => throw new InvalidOperationException(),
        }).ToArray();

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Failed, fixture.Job.Status);
        Assert.Contains("tim", fixture.Job.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(0, fixture.CountedLibrary.SaveCalls);
        Assert.Equal(original.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.Equal(bytes, fixture.Bytes(original));
        Assert.True(fixture.Read().Succeeded);
        Assert.Equal(2, fixture.Read().Coverage.Gaps.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisedOverlapIsSavedEvenWhenResponseAddsNoNewCandles(bool correctionsOnly)
    {
        using var fixture = new Fixture(cutoffAfter: TimeSpan.FromMinutes(3));
        HistoricalDatasetInfo original = fixture.Seed(index => index is not (1 or 10));
        HistoricalCandle[] snapshot = fixture.Library.Read(original.DatasetHash).Candles.ToArray();
        DateTimeOffset overlap = fixture.From.AddSeconds(30);
        HistoricalCandle corrected = Candle(overlap) with { Open = 10.25m, Close = 10.5m, Volume = 50 };
        fixture.Provider.Transform = candles => correctionsOnly ? [corrected] :
            candles.Select(candle => candle.StartsAtUtc == overlap ? corrected : candle).ToArray();

        await fixture.Drain();

        Assert.Equal(correctionsOnly ? CollectionJobStatus.Partial : CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.Equal(1, fixture.CountedLibrary.SaveCalls);
        Assert.Equal(corrected, fixture.Read().Candles.Single(candle => candle.StartsAtUtc == overlap));
        Assert.Equal(snapshot, fixture.Library.Read(original.DatasetHash).Candles);
        Assert.NotEqual(original.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.Equal(correctionsOnly ? 2 : 0, fixture.Read().Coverage.Gaps.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task ZeroAndNullFieldsKeepStoredValuesWhileOtherUpdatesAndGapsAreSaved(int? volume)
    {
        using var fixture = new Fixture(cutoffAfter: TimeSpan.FromMinutes(3));
        fixture.Seed(index => index is not (1 or 10));
        DateTimeOffset overlap = fixture.From.AddSeconds(30);
        fixture.Provider.Transform = candles => candles.Select(candle => candle.StartsAtUtc == overlap
            ? candle with { Open = 0, High = 0, Low = 0, Close = 10.5m, Volume = volume } : candle).ToArray();

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Complete, fixture.Job.Status);
        Assert.Equal(Candle(overlap) with { Close = 10.5m }, fixture.Read().Candles.Single(candle => candle.StartsAtUtc == overlap));
        Assert.Empty(fixture.Read().Coverage.Gaps);
    }

    [Fact]
    public async Task ZeroResponsesNeverEraseStoredCandlesOrFillGaps()
    {
        using var fixture = new Fixture(cutoffAfter: TimeSpan.FromMinutes(3));
        HistoricalDatasetInfo original = fixture.Seed(index => index is not (1 or 10));
        byte[] bytes = fixture.Bytes(original);
        fixture.Provider.Transform = candles => candles.Select(candle => candle with
            { Open = 0, High = 0, Low = 0, Close = 0, Volume = 0 }).ToArray();

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Partial, fixture.Job.Status);
        Assert.Equal(0, fixture.CountedLibrary.SaveCalls);
        Assert.Equal(bytes, fixture.Bytes(original));
        Assert.Equal(2, fixture.Read().Coverage.Gaps.Count);
    }

    [Fact]
    public async Task DuplicateReturnedOverlapIsRejectedEvenWhenItsPricesAgree()
    {
        using var fixture = new Fixture();
        HistoricalDatasetInfo original = fixture.Seed(index => index is not (1 or 10));
        byte[] bytes = fixture.Bytes(original);
        fixture.Provider.Transform = candles =>
        {
            HistoricalCandle overlap = candles.Single(candle => candle.StartsAtUtc == fixture.From.AddSeconds(30));
            return [overlap, overlap];
        };

        await fixture.Drain();

        Assert.Equal(CollectionJobStatus.Failed, fixture.Job.Status);
        Assert.Contains("duplicate", fixture.Job.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.CountedLibrary.SaveCalls);
        Assert.Equal(original.DatasetHash, Assert.Single(fixture.Library.Scan().Datasets).DatasetHash);
        Assert.Equal(bytes, fixture.Bytes(original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartResumesAfterThePersistedBatchWithOrWithoutNewCandles(bool returnsNewCandles)
    {
        using var fixture = new Fixture();
        fixture.Seed(index => index % 9 != 4);
        if (!returnsNewCandles) fixture.Provider.Transform = _ => [];
        await fixture.Collector.TickAsync(true);
        HistoricalDataRequest first = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(first.ThroughUtc, fixture.Job.NextGapFromUtc);
        Assert.Equal(CollectionJobStatus.Pending, fixture.Job.Status);

        fixture.Restart();
        await fixture.Drain();

        Assert.All(fixture.Provider.Requests.Skip(1), request => Assert.True(request.FromUtc >= first.ThroughUtc));
        Assert.Equal(fixture.Provider.Requests.Count, fixture.Provider.Requests.Distinct().Count());
        Assert.InRange(fixture.Provider.Requests.Count, 1, 6);
        Assert.Equal(returnsNewCandles ? CollectionJobStatus.Complete : CollectionJobStatus.Partial, fixture.Job.Status);
        Assert.Equal(returnsNewCandles ? 5760 : 5120, fixture.Read().Candles.Count);
    }

    private static HistoricalCandle Candle(DateTimeOffset start) =>
        new(start, start.AddSeconds(15), start.AddSeconds(15), 10, 11, 9, 10, 100);

    private sealed class Fixture : IDisposable
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        public Fixture(DateOnly? day = null, TimeSpan? cutoffAfter = null)
        {
            Day = day ?? new(2026, 9, 8);
            CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(Day, "24_5");
            From = window.FromUtc;
            Through = cutoffAfter is { } cutoff ? From + cutoff : window.ThroughUtc;
            Clock = new(window.ThroughUtc.AddHours(1));
            Library = new(Path.Combine(_temporaryRoot, "PriceSentinel-gap-batching-" + Guid.NewGuid().ToString("N")));
            CountedLibrary = new(Library);
            AllCandles = Enumerable.Range(0, (int)((Through - From).TotalSeconds / 15)).Select(index => Candle(From.AddSeconds(15 * index))).ToArray();
            Provider = new(Clock);
            Store.Save(new()
            {
                Settings = new() { LibraryRootPath = Library.RootPath },
                Jobs = [new()
                {
                    Symbol = "USO", ProviderInstrumentId = "USO-id", SessionDate = Day, SessionBounds = "24_5",
                    LibraryRootPath = Library.RootPath, QueuedAtUtc = Clock.Now,
                    RequestedThroughUtc = cutoffAfter is null ? null : Through,
                }],
            });
            Restart();
        }
        public DateOnly Day { get; }
        public DateTimeOffset From { get; }
        public DateTimeOffset Through { get; }
        public HistoricalCandle[] AllCandles { get; }
        public Clock Clock { get; }
        public MemoryStore Store { get; } = new();
        public Provider Provider { get; }
        public JsonMarketDataLibrary Library { get; }
        public CountingLibrary CountedLibrary { get; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public CollectionJob Job => Assert.Single(Collector.State.Jobs);
        public HistoricalDatasetInfo Seed(Func<int, bool> include, bool includeClosedHoursInRequestedCoverage = false)
        {
            DateTimeOffset from = includeClosedHoursInRequestedCoverage ?
                new(TimeZoneInfo.ConvertTimeToUtc(Day.ToDateTime(TimeOnly.MinValue), Eastern), TimeSpan.Zero) : From;
            DateTimeOffset through = includeClosedHoursInRequestedCoverage ?
                new(TimeZoneInfo.ConvertTimeToUtc(Day.AddDays(1).ToDateTime(TimeOnly.MinValue), Eastern), TimeSpan.Zero) : Through;
            return Assert.Single(Library.Save(new("test", "USO-id", "USO", 15, "split", "robinhood-split-unversioned",
                "24_5", Clock.Now, from, through, AllCandles.Where((_, index) => include(index)).ToArray())));
        }
        public byte[] Bytes(HistoricalDatasetInfo dataset) => File.ReadAllBytes(Path.Combine(Library.RootPath, dataset.RelativePath));
        public HistoricalDataQueryResult Read() => Library.Query(new("USO", From, Through, 15,
            SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage, IncludeCompatibleSessions: true));
        public void Restart() => Collector = new(Store, Provider, _ => CountedLibrary, Clock, new()
        {
            MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero,
        });
        public async Task Drain()
        {
            for (int i = 0; i < 20; i++)
                if (await Collector.TickAsync(true) == CollectionBatchResult.Idle) return;
            Assert.Fail("Gap collection did not finish within 20 ticks.");
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(Library.RootPath);
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("PriceSentinel-gap-batching-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean a directory outside this fixture's temporary root.");
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
        public Func<HistoricalCandle[], HistoricalCandle[]> Transform { get; set; } = candles => candles;
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            HistoricalCandle[] candles = Enumerable.Range(0, (int)((request.ThroughUtc - request.FromUtc).TotalSeconds / 15))
                .Select(index => Candle(request.FromUtc.AddSeconds(15 * index))).ToArray();
            return Task.FromResult(new HistoricalDownload("test", "USO-id", "USO", 15, "split", "robinhood-split-unversioned",
                request.SessionBounds, clock.Now, request.FromUtc, request.ThroughUtc, Transform(candles)));
        }
    }

    private sealed class CountingLibrary(IMarketDataLibrary inner) : IMarketDataLibrary
    {
        public string RootPath => inner.RootPath;
        public int QueryCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public MarketDataLibraryScan Scan() => inner.Scan();
        public HistoricalDataset Read(string datasetHash) => inner.Read(datasetHash);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            QueryCalls++;
            return inner.Query(query);
        }
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
        {
            SaveCalls++;
            return inner.Save(download);
        }
    }
}
