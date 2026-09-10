using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class CollectionContinuityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PriceSentinel-continuity-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Day = new(2026, 9, 4);
    private static readonly CollectionSessionWindow Window = CollectionSchedule.GetSessionWindow(Day);
    private static readonly DateTimeOffset FetchedAt = Window.ThroughUtc.AddHours(1);
    private static readonly HistoricalCandle[] FullDay = Enumerable.Range(0, 1560).Select(index =>
    {
        DateTimeOffset at = Window.FromUtc.AddSeconds(index * 15);
        decimal price = 100m + index % 20 / 100m;
        return new HistoricalCandle(at, at.AddSeconds(15), at.AddSeconds(15), price, price + .1m,
            price - .1m, price, index % 2 == 0 ? index : null);
    }).ToArray();
    private JsonMarketDataLibrary Library => new(_directory);

    [Fact]
    public async Task SavedPrefix_ExtendsFromFirstMissingCandleAndPreservesPinnedOriginal()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download() with
        {
            RequestedThroughUtc = FullDay[10].StartsAtUtc, Candles = FullDay.Take(10).ToArray(),
        }));
        byte[] originalBytes = File.ReadAllBytes(Path.Combine(_directory, original.RelativePath));
        var provider = new Provider();
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        HistoricalDataRequest request = Assert.Single(provider.Requests);
        Assert.Equal(FullDay[10].StartsAtUtc, request.FromUtc);
        Assert.Equal(Window.ThroughUtc, request.ThroughUtc);
        Assert.Equal(15, request.SourceIntervalSeconds);
        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        HistoricalDataset completed = Library.Read(Assert.Single(job.DatasetHashes));
        Assert.Equal(FullDay, completed.Candles);
        Assert.True(completed.Coverage.Complete);
        Assert.Equal(Window.FromUtc, completed.Coverage.RequestedFromUtc);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(_directory, ".archive", original.DatasetHash + ".json")));
        Assert.Single(Library.Scan().Datasets);
        Assert.Equal(10, Library.Read(original.DatasetHash).Candles.Count);
    }

    [Fact]
    public async Task InteriorGap_IsFilledEvenWhenLaterSavedCandlesReachSessionClose()
    {
        const int missingIndex = 17;
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download() with
        {
            Candles = FullDay.Where((_, index) => index != missingIndex).ToArray(),
        }));
        var provider = new Provider { Respond = request => Response(request) with { Candles = [FullDay[missingIndex]] } };
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        Assert.Equal(FullDay[missingIndex].StartsAtUtc, Assert.Single(provider.Requests).FromUtc);
        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        HistoricalDataset completed = Library.Read(Assert.Single(job.DatasetHashes));
        Assert.Equal(FullDay, completed.Candles);
        Assert.True(completed.Coverage.Complete);
        Assert.Equal(FullDay.Length - 1, Library.Read(original.DatasetHash).Candles.Count);
    }

    [Fact]
    public async Task EmptyMissingRange_PreservesSavedDataAndReportsPartialWithoutCoarseFallback()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download() with { Candles = FullDay.Take(10).ToArray() }));
        byte[] originalBytes = File.ReadAllBytes(Path.Combine(_directory, original.RelativePath));
        var provider = new Provider { Respond = request => Response(request) with { Candles = [] } };
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        Assert.Equal(15, Assert.Single(provider.Requests).SourceIntervalSeconds);
        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Partial, job.Status);
        Assert.Equal(original.DatasetHash, Assert.Single(job.DatasetHashes));
        Assert.Equal(15, job.ActualSourceIntervalSeconds);
        Assert.Contains("gap remains unresolved", job.Error);
        Assert.Single(Library.Scan().Datasets);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(_directory, original.RelativePath)));
    }

    [Theory]
    [InlineData("instrument")]
    [InlineData("provider")]
    [InlineData("adjustment-policy")]
    [InlineData("adjustment-basis")]
    [InlineData("source-interval")]
    public async Task IncompatibleDownload_CannotBlendWithSavedCandlesOrWriteARevision(string mismatch)
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download() with { Candles = FullDay.Take(10).ToArray() }));
        byte[] originalBytes = File.ReadAllBytes(Path.Combine(_directory, original.RelativePath));
        var provider = new Provider
        {
            Respond = request => mismatch switch
            {
                "instrument" => Response(request) with { InstrumentId = "other-instrument" },
                "provider" => Response(request) with { Provider = "Other provider" },
                "adjustment-policy" => Response(request) with { AdjustmentPolicy = "unadjusted" },
                "adjustment-basis" => Response(request) with { AdjustmentBasis = "robinhood-split-revision:changed" },
                "source-interval" => Response(request) with { SourceIntervalSeconds = 30 },
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
            },
        };
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        Assert.Equal(CollectionJobStatus.Failed, Assert.Single(collector.State.Jobs).Status);
        Assert.Single(provider.Requests);
        Assert.Equal(original.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(_directory, original.RelativePath)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(10)]
    public async Task ChangedOverlappingCandle_UpdatesValuesAndPreservesArchivedSnapshot(int? volume)
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download() with
        {
            Candles = FullDay.Where((_, index) => index != 17).ToArray(),
        }));
        byte[] originalBytes = File.ReadAllBytes(Path.Combine(_directory, original.RelativePath));
        var provider = new Provider
        {
            Respond = request => Response(request) with
            {
                Candles = [FullDay[17], FullDay[18] with { Open = 0, Close = FullDay[18].Close + .01m, Volume = volume }],
            },
        };
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        HistoricalDataset current = Library.Read(Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.NotEqual(original.DatasetHash, current.DatasetHash);
        Assert.Equal(1560, current.Candles.Count);
        Assert.Equal(FullDay[18] with { Close = FullDay[18].Close + .01m, Volume = volume is > 0 ? volume : FullDay[18].Volume },
            current.Candles.Single(candle => candle.StartsAtUtc == FullDay[18].StartsAtUtc));
        Assert.Equal(FullDay[18], Library.Read(original.DatasetHash).Candles.Single(candle => candle.StartsAtUtc == FullDay[18].StartsAtUtc));
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(_directory, ".archive", original.DatasetHash + ".json")));
    }

    [Fact]
    public async Task OnlyZeroPricePlaceholders_DoNotSaveOrReportUsableHistory()
    {
        var provider = new Provider
        {
            Respond = request => Response(request) with
            {
                Candles = [FullDay[0] with { Open = 0, High = 0, Low = 0, Close = 0, Volume = 0 }],
            },
        };
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Unavailable, job.Status);
        Assert.Null(job.ActualSourceIntervalSeconds);
        Assert.False(job.ReceivedCandlesThisRun);
        Assert.Empty(job.DatasetHashes);
        Assert.Empty(Library.Scan().Datasets);
    }

    [Fact]
    public async Task CompleteSavedSession_IsReusedWithoutAnyProviderRequestOrNewFile()
    {
        HistoricalDatasetInfo original = Assert.Single(Library.Save(Download()));
        var provider = new Provider();
        MarketDataCollector collector = CreateCollector(provider);

        await RunAsync(collector);

        Assert.Empty(provider.Requests);
        CollectionJob job = Assert.Single(collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Complete, job.Status);
        Assert.Equal(original.DatasetHash, Assert.Single(job.DatasetHashes));
        Assert.Equal(original.DatasetHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
    }

    private MarketDataCollector CreateCollector(Provider provider)
    {
        var settings = new CollectionSettings
        {
            LibraryRootPath = _directory,
            Lists = [new(Guid.NewGuid(), "Tracked", true, [new("NFLX", ProviderInstrumentId: "instrument-nflx")])],
        };
        return new(new MemoryStore(new() { Settings = settings }), provider, root => new JsonMarketDataLibrary(root),
            new Clock(), new() { MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero });
    }

    private static async Task RunAsync(MarketDataCollector collector)
    {
        await collector.QueueManualAsync(["NFLX"], Day, Day);
        await collector.TickAsync(true);
    }

    private static HistoricalDownload Download() => new("Robinhood", "instrument-nflx", "NFLX", 15,
        "split", "robinhood-split-unversioned", "regular", FetchedAt, Window.FromUtc, Window.ThroughUtc, FullDay);

    private static HistoricalDownload Response(HistoricalDataRequest request) => Download() with
    {
        FetchedAtUtc = FetchedAt.AddHours(1), RequestedFromUtc = request.FromUtc,
        RequestedThroughUtc = request.ThroughUtc,
        Candles = FullDay.Where(c => c.StartsAtUtc >= request.FromUtc && c.EndsAtUtc <= request.ThroughUtc).ToArray(),
    };

    private sealed class Provider : IMarketHistoryProvider
    {
        public List<HistoricalDataRequest> Requests { get; } = [];
        public Func<HistoricalDataRequest, HistoricalDownload> Respond { get; init; } = Response;
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class MemoryStore(CollectionState state) : ICollectionStateStore
    {
        public CollectionState Load() => state;
        public void Save(CollectionState next) => state = next;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => FetchedAt.AddHours(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
