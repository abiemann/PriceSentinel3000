using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class ReplayHistoryAvailabilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-availability-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
    private readonly Provider _provider = new();
    private JsonMarketDataLibrary Library => new(_root);
    private HistoricalDataQuery Query => new("MSFT", Start, Start.AddMinutes(2), AdjustmentPolicy: "split", SessionBounds: "regular");
    private ReplayHistoryAvailabilityService Service => new(Library, _provider);

    [Fact]
    public async Task CompleteLocalFine_IsReadyWithoutNetworkOrArchiveChanges()
    {
        string hash = Assert.Single(Library.Save(Download(15))).DatasetHash;
        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.True(result.IsLocal);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Equal(hash, Assert.Single(result.Datasets).DatasetHash);
        Assert.Null(result.PendingDownload);
        Assert.Empty(_provider.Requests);
        Assert.Equal(hash, Assert.Single(Library.Scan().Datasets).DatasetHash);
    }

    [Fact]
    public async Task QueryWindow_NotDailyFileLabel_DeterminesCompleteCoverage()
    {
        Library.Save(Download(15) with { Candles = Download(15).Candles.Take(4).ToArray() });
        ReplayHistoryAvailability complete = await Service.CheckAsync(Query with { ThroughUtc = Start.AddMinutes(1) }, true, default);
        ReplayHistoryAvailability partial = await Service.CheckAsync(Query, true, default);

        Assert.True(complete.Complete);
        Assert.False(partial.Complete);
        Assert.True(partial.HasData);
        Assert.Equal(4, partial.Candles.Count);
        Assert.Equal(8, partial.Coverage.ExpectedCandleCount);
        Assert.Contains(partial.Diagnostics, item => item.Code == "partial_replay_history");
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ProviderFine_ReplacesIncompleteLocalOnlyForPreparedChoice_WithoutWriting()
    {
        string partialHash = Assert.Single(Library.Save(Download(15) with { Candles = [Download(15).Candles[1]] })).DatasetHash;
        _provider.Downloads[15] = Download(15);

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.False(result.IsLocal);
        Assert.Equal("provider", result.Source);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.NotNull(result.PendingDownload);
        Assert.Equal(partialHash, Assert.Single(Library.Scan().Datasets).DatasetHash);
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Fact]
    public async Task ProviderCheck_KeepsDataInMemoryUntilStart_AndStartNeverDownloadsAgain()
    {
        _provider.Downloads[15] = Download(15) with
        {
            Candles = Download(15).Candles.Select(item => item with { Volume = null, Close = 100.123456789m }).ToArray(),
        };
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        Assert.False(Directory.Exists(_root));
        _provider.Error = new HttpRequestException("Connection disappeared after checking");

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal("provider-saved-library", loaded.Source);
        Assert.Single(_provider.Requests);
        Assert.Equal(prepared.Candles, loaded.Candles);
        string hash = Assert.Single(loaded.Datasets).DatasetHash;
        Assert.Equal(prepared.Candles, Library.Read(hash).Candles);
        Assert.All(loaded.Candles, item => Assert.Null(item.Volume));
        Assert.Contains(loaded.Diagnostics, item => item.Code == "unknown_volume");
    }

    [Fact]
    public async Task LocalStart_PinsCheckedRevision_EvenWhenNewerRevisionAppears()
    {
        string checkedHash = Assert.Single(Library.Save(Download(15))).DatasetHash;
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        Library.Save(Download(15) with
        {
            FetchedAtUtc = Start.AddDays(2),
            Candles = Download(15).Candles.Select(item => item with { Close = 100.5m }).ToArray(),
        });

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(checkedHash, Assert.Single(loaded.Datasets).DatasetHash);
        Assert.All(loaded.Candles, item => Assert.Equal(100m, item.Close));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ProviderCollectionChanges_DoNotChangePreparedCandles()
    {
        HistoricalCandle[] mutable = Download(15).Candles.ToArray();
        _provider.Downloads[15] = Download(15) with { Candles = mutable };
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        mutable[0] = mutable[0] with { Close = 100.5m };

        LibraryReplayHistoryResult loaded = await Service.LoadPreparedAsync(prepared, default);

        Assert.Equal(100m, loaded.Candles[0].Close);
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task RemovedPreparedFile_FailsBeforeStartWithoutSubstitution()
    {
        Library.Save(Download(15));
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        File.Delete(Path.Combine(_root, Assert.Single(prepared.Datasets).RelativePath));
        _provider.Downloads[15] = Download(15);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.LoadPreparedAsync(prepared, default));
        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task CompleteLocalCoarse_IsPreferredToIncompleteFineDuringExplicitPreflight(int interval)
    {
        Library.Save(Download(15) with { Candles = [Download(15).Candles[0]] });
        Library.Save(Download(interval));

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, true, default);

        Assert.True(result.Complete);
        Assert.True(result.IsLocal);
        Assert.Equal(interval, result.SourceIntervalSeconds);
        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task BrokerFallback_ProbesGenuineIntervalsInOrderAndPreservesActualResolution(int interval)
    {
        _provider.Downloads[15] = Download(15) with { Candles = [Download(15).Candles[0]] };
        _provider.Downloads[interval] = Download(interval);

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.False(result.IsLocal);
        Assert.Equal(interval, result.SourceIntervalSeconds);
        Assert.Equal(new[] { 15, 30, 60 }.Where(item => item <= interval), _provider.Requests.Select(item => item.SourceIntervalSeconds));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task LocalTwoMinute_IsLastChoice_AndNeverRequestedFromBroker()
    {
        Library.Save(Download(120));

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.True(result.Complete);
        Assert.Equal(120, result.SourceIntervalSeconds);
        Assert.True(result.IsLocal);
        Assert.Equal(new[] { 15, 30, 60 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Fact]
    public async Task NoCompleteSource_ReturnsFinestPartialAndItsExactGaps()
    {
        Library.Save(Download(15) with { Candles = [Download(15).Candles[0]] });
        _provider.Downloads[15] = Download(15) with { Candles = Download(15).Candles.Take(3).ToArray() };
        _provider.Downloads[30] = Download(30) with { Candles = Download(30).Candles.Take(3).ToArray() };

        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);

        Assert.False(result.Complete);
        Assert.True(result.HasData);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Equal(3, result.Candles.Count);
        Assert.Equal(new HistoricalGap(Start.AddSeconds(45), Start.AddMinutes(2)), Assert.Single(result.Coverage.Gaps));
        Assert.Contains(result.Diagnostics, item => item.Code == "partial_replay_history");
    }

    [Fact]
    public async Task NoData_IsDistinctFromPartialAndDoesNotCreateArchiveFiles()
    {
        ReplayHistoryAvailability result = await Service.CheckAsync(Query, false, default);
        Assert.False(result.HasData);
        Assert.False(result.Complete);
        Assert.Single(result.Coverage.Gaps);
        Assert.False(Directory.Exists(_root));
        Assert.Empty((await Service.LoadPreparedAsync(result, default)).Candles);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExplicitCoarsePins_DoNotProbeFineBrokerOrSelectFineLocal()
    {
        string pinned = Assert.Single(Library.Save(Download(60))).DatasetHash;
        Library.Save(Download(15));

        ReplayHistoryAvailability result = await Service.CheckAsync(Query with { PinnedHashes = [pinned] }, false, default);

        Assert.Equal("pinned-library", result.Source);
        Assert.Equal(60, result.SourceIntervalSeconds);
        Assert.Equal(pinned, Assert.Single(result.Datasets).DatasetHash);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task PinnedPartialFine_RemainsPartialEvenWhenBrokerHasCompleteData()
    {
        string pinned = Assert.Single(Library.Save(Download(15) with { Candles = [Download(15).Candles[1]] })).DatasetHash;
        _provider.Downloads[15] = Download(15);

        ReplayHistoryAvailability result = await Service.CheckAsync(Query with { PinnedHashes = [pinned] }, false, default);

        Assert.True(result.HasData);
        Assert.False(result.Complete);
        Assert.Equal(pinned, Assert.Single(result.Datasets).DatasetHash);
        Assert.Contains(result.Diagnostics, item => item.Code == "partial_replay_history");
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task ConflictingRevisions_RequireExplicitPolicyInsteadOfBrokerSubstitution()
    {
        Library.Save(Download(15));
        string newer = Assert.Single(Library.Save(Download(15) with
        {
            FetchedAtUtc = Start.AddDays(2),
            Candles = Download(15).Candles.Select(item => item with { Close = 100.5m }).ToArray(),
        })).DatasetHash;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CheckAsync(Query, false, default));
        ReplayHistoryAvailability result = await Service.CheckAsync(Query with { RevisionPolicy = HistoricalRevisionPolicy.LatestFetched }, false, default);

        Assert.Equal(newer, Assert.Single(result.Datasets).DatasetHash);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task BrokerFailure_IsUnknown_NotProofThatOnlyCoarseHistoryExists()
    {
        Library.Save(Download(60));
        _provider.Error = new MarketDataConnectionUnavailableException("Not connected");
        await Assert.ThrowsAsync<MarketDataConnectionUnavailableException>(() => Service.CheckAsync(Query, false, default));
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("interval")]
    [InlineData("range")]
    [InlineData("bounds")]
    [InlineData("adjustment")]
    [InlineData("provider")]
    public async Task WrongBrokerProvenance_CannotProduceAvailability(string field)
    {
        HistoricalDownload wrong = field switch
        {
            "symbol" => Download(15) with { Symbol = "SOXL" },
            "interval" => Download(30),
            "range" => Download(15) with { RequestedFromUtc = Start.AddMinutes(-1) },
            "bounds" => Download(15) with { SessionBounds = "extended" },
            "adjustment" => Download(15) with { AdjustmentPolicy = "none" },
            _ => Download(15) with { Provider = "other-provider" },
        };
        _provider.Downloads[15] = wrong;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CheckAsync(Query with { Provider = "Robinhood" }, false, default));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("price")]
    [InlineData("unfinalized")]
    [InlineData("future")]
    public async Task InvalidBrokerCandles_CannotTurnDateGreen(string kind)
    {
        HistoricalCandle[] candles = Download(15).Candles.ToArray();
        candles[1] = kind switch
        {
            "duplicate" => candles[0],
            "price" => candles[1] with { Close = -1m },
            "unfinalized" => candles[1] with { AvailableAtUtc = candles[1].EndsAtUtc.AddSeconds(1) },
            _ => candles[1] with { EndsAtUtc = Start.AddDays(2) },
        };
        _provider.Downloads[15] = Download(15) with { Candles = candles };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.CheckAsync(Query, false, default));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancelledCheckAndStart_DoNotRequestOrWrite()
    {
        _provider.Downloads[15] = Download(15);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.CheckAsync(Query, false, cancellation.Token));
        Assert.Empty(_provider.Requests);
        ReplayHistoryAvailability prepared = await Service.CheckAsync(Query, false, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.LoadPreparedAsync(prepared, cancellation.Token));
        Assert.False(Directory.Exists(_root));
    }

    private static HistoricalDownload Download(int interval) => new("Robinhood", "test-msft", "MSFT", interval,
        "split", "robinhood-split-unversioned", "regular", Start.AddDays(1), Start, Start.AddMinutes(2),
        Enumerable.Range(0, 120 / interval).Select(index =>
        {
            DateTimeOffset at = Start.AddSeconds(index * interval);
            return new HistoricalCandle(at, at.AddSeconds(interval), at.AddSeconds(interval), 100m, 101m, 99m, 100m, 1000m);
        }).ToArray());

    private sealed class Provider : IMarketHistoryProvider
    {
        public Dictionary<int, HistoricalDownload> Downloads { get; } = [];
        public List<HistoricalDataRequest> Requests { get; } = [];
        public Exception? Error { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Error is not null) throw Error;
            return Task.FromResult(Downloads.GetValueOrDefault(request.SourceIntervalSeconds) ??
                Download(request.SourceIntervalSeconds) with { Candles = [] });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
