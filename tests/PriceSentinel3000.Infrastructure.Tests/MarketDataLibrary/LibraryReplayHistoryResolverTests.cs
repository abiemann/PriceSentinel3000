using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class LibraryReplayHistoryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-replay-library-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
    private readonly Provider _provider = new();
    private int _connections;
    private JsonMarketDataLibrary Library => new(_root);
    private HistoricalDataQuery Query => new("MSFT", Start, Start.AddMinutes(2), AdjustmentPolicy: "split", SessionBounds: "regular");
    private LibraryReplayHistoryResolver Resolver => new(Library, _provider, _ => { _connections++; return Task.CompletedTask; });

    [Fact]
    public async Task CompleteLocal_PreservesExactCandlesAndHashesWithoutAnyConnection()
    {
        HistoricalDownload download = Download(15) with
        {
            Candles = Download(15).Candles.Select(item => item with
            {
                Open = 100.12345678901234567890123456m, High = 102m, Volume = null,
            }).ToArray(),
        };
        string hash = Assert.Single(Library.Save(download)).DatasetHash;
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);

        Assert.Equal("local-library", result.Source);
        Assert.Equal(hash, Assert.Single(result.Datasets).DatasetHash);
        Assert.Equal(download.Candles, result.Candles);
        Assert.True(result.Coverage.Complete);
        Assert.All(result.Candles, item => Assert.Null(item.Volume));
        Assert.Contains(result.Diagnostics, item => item.Code == "unknown_volume");
        Assert.Contains(result.Diagnostics, item => item.Code == "unversioned_adjustment");
        AssertNoNetwork();
    }

    [Fact]
    public async Task PartialFineLocal_IsUsedWithGapsInsteadOfCoarseSubstitutionOrLogin()
    {
        HistoricalDownload fine = Download(15) with { Candles = [Download(15).Candles[1]] };
        Library.Save(fine);
        Library.Save(Download(30));
        _provider.Downloads[15] = Download(15);

        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);

        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Equal(fine.Candles, result.Candles);
        Assert.False(result.Coverage.Complete);
        Assert.Equal(2, result.Coverage.Gaps.Count);
        Assert.Contains(result.Diagnostics, item => item.Code == "partial_replay_history");
        AssertNoNetwork();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task Offline_CoarserLocalNeverConnectsOrInventsFinerBars(int interval)
    {
        HistoricalDownload download = Download(interval);
        Library.Save(download);
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, true, default);
        Assert.Equal(interval, result.SourceIntervalSeconds);
        Assert.Equal(download.Candles, result.Candles);
        AssertNoNetwork();
    }

    [Fact]
    public async Task Online_ProviderFineIsPreferredOverLocalCoarseAndSavedBeforeUse()
    {
        Library.Save(Download(30));
        _provider.Downloads[15] = Download(15);
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);
        Assert.Equal("provider-saved-library", result.Source);
        Assert.Equal(15, result.SourceIntervalSeconds);
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
        Assert.Equal(1, _connections);
        Assert.Equal(result.Candles, Library.Read(Assert.Single(result.Datasets).DatasetHash).Candles);
    }

    [Fact]
    public async Task FineProviderEmpty_UsesLocalThirtyBeforeRequestingThirty()
    {
        Library.Save(Download(30));
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);
        Assert.Equal(30, result.SourceIntervalSeconds);
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
        Assert.Equal(1, _connections);
    }

    [Fact]
    public async Task EmptyFineSources_FallBackInOrderToExactProviderMinute()
    {
        _provider.Downloads[60] = Download(60);
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);
        Assert.Equal(60, result.SourceIntervalSeconds);
        Assert.Equal(new[] { 15, 30, 60 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
        Assert.All(_provider.Requests, item =>
        {
            Assert.Equal(Start, item.FromUtc);
            Assert.Equal(Start.AddMinutes(2), item.ThroughUtc);
            Assert.Equal("regular", item.SessionBounds);
            Assert.Equal("split", item.AdjustmentPolicy);
        });
        Assert.Equal(1, _connections);
        Assert.Equal(2, result.Candles.Count);
    }

    [Fact]
    public async Task LocalTwoMinute_IsLastAndProviderNeverReceivesUnsupportedTwoMinuteRequest()
    {
        Library.Save(Download(120));
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);
        Assert.Equal(120, result.SourceIntervalSeconds);
        Assert.Single(result.Candles);
        Assert.Equal(new[] { 15, 30, 60 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Fact]
    public async Task NoData_ReturnsEmptyWithExplicitCoverage()
    {
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query, false, default);
        Assert.Empty(result.Candles);
        Assert.False(result.Coverage.Complete);
        Assert.Single(result.Coverage.Gaps);
        Assert.Equal(new[] { 15, 30, 60 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Fact]
    public async Task PinnedCoarseRevision_OverridesFinerLocalAndNetworkRegardlessOfOnlineSetting()
    {
        string hash = Assert.Single(Library.Save(Download(30))).DatasetHash;
        Library.Save(Download(15));
        LibraryReplayHistoryResult result = await Resolver.ResolveAsync(Query with { PinnedHashes = [hash] }, false, default);
        Assert.Equal("pinned-library", result.Source);
        Assert.Equal(30, result.SourceIntervalSeconds);
        Assert.Equal(hash, Assert.Single(result.Datasets).DatasetHash);
        AssertNoNetwork();
    }

    [Fact]
    public async Task MissingPinnedFile_FailsWithoutSubstitutionOrNetwork()
    {
        Library.Save(Download(15));
        await Assert.ThrowsAsync<InvalidDataException>(() => Resolver.ResolveAsync(Query with { PinnedHashes = [new string('a', 64)] }, false, default));
        AssertNoNetwork();
    }

    [Fact]
    public async Task MixedPinnedIntervals_AreRejectedWithoutNetwork()
    {
        string fine = Assert.Single(Library.Save(Download(15))).DatasetHash;
        string coarse = Assert.Single(Library.Save(Download(30))).DatasetHash;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver.ResolveAsync(Query with { PinnedHashes = [fine, coarse] }, false, default));
        AssertNoNetwork();
    }

    [Fact]
    public async Task Conflict_RequiresExplicitLatestFetchedOrExactOldHash()
    {
        string old = Assert.Single(Library.Save(Download(15))).DatasetHash;
        HistoricalDownload revised = Download(15) with
        {
            FetchedAtUtc = Start.AddDays(2),
            Candles = Download(15).Candles.Select(item => item with { Close = 100.5m }).ToArray(),
        };
        string latest = Assert.Single(Library.Save(revised)).DatasetHash;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver.ResolveAsync(Query, false, default));
        LibraryReplayHistoryResult newest = await Resolver.ResolveAsync(Query with { RevisionPolicy = HistoricalRevisionPolicy.LatestFetched }, false, default);
        Assert.Equal(latest, Assert.Single(newest.Datasets).DatasetHash);
        LibraryReplayHistoryResult pinned = await Resolver.ResolveAsync(Query with { PinnedHashes = [old] }, false, default);
        Assert.All(pinned.Candles, item => Assert.Equal(100m, item.Close));
        AssertNoNetwork();
    }

    [Fact]
    public async Task ProviderFailure_IsNotTreatedAsProofThatFineDataIsUnavailable()
    {
        Library.Save(Download(30));
        _provider.Error = new HttpRequestException("Network unavailable");
        await Assert.ThrowsAsync<HttpRequestException>(() => Resolver.ResolveAsync(Query, false, default));
        Assert.Equal(new[] { 15 }, _provider.Requests.Select(item => item.SourceIntervalSeconds));
    }

    [Fact]
    public async Task UnexpectedProviderInterval_FailsBeforeSaving()
    {
        _provider.Downloads[15] = Download(30);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver.ResolveAsync(Query, false, default));
        Assert.Empty(Library.Scan().Datasets);
    }

    [Fact]
    public async Task Cancellation_DoesNotConnectOrFetch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolver.ResolveAsync(Query, false, cancellation.Token));
        AssertNoNetwork();
    }

    private void AssertNoNetwork()
    {
        Assert.Equal(0, _connections);
        Assert.Empty(_provider.Requests);
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
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken token)
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
