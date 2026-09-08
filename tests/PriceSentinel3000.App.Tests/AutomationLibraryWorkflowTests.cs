using System.IO;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task AutomationLibrary_ReadsExactArchivedCandlesOfflineWithoutActiveSessionEvenInLiveMode() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        await using var fixture = new LibraryAutomationFixture(workspace.ViewModel);
        HistoricalDatasetInfo dataset = fixture.Save(110);
        workspace.ViewModel.RequestModeSelection(TradingMode.Live);

        var first = await Automate(workspace.ViewModel, "library_candles", new { datasetHash = dataset.DatasetHash, limit = 100 });
        Assert.True(first.Success, first.Error);
        var page = first.Result!.Value;
        Assert.Equal(110, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(100, page.GetProperty("nextOffset").GetInt32());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        var candle = page.GetProperty("candles")[0];
        Assert.Equal("80.12345678901234567890123456", candle.GetProperty("open").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, candle.GetProperty("volume").ValueKind);
        Assert.Equal(LibraryAutomationFixture.At.AddSeconds(15), candle.GetProperty("availableAtUtc").GetDateTimeOffset());
        var second = await Automate(workspace.ViewModel, "library_candles", new { datasetHash = dataset.DatasetHash, offset = 100, limit = 100 });
        Assert.Equal(10, second.Result!.Value.GetProperty("candles").GetArrayLength());
        Assert.False(second.Result!.Value.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.False(workspace.ViewModel.IsSessionRunning);
        Assert.DoesNotContain(fixture.Root, page.GetRawText());
        Assert.DoesNotContain("private-list-id", page.GetRawText());
        Assert.False(page.TryGetProperty("relativePath", out _));
    });

    [Fact]
    public Task AutomationLibrary_DatasetPaginationPinsCatalogAndDiagnosesCorruptionWithoutPaths() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        await using var fixture = new LibraryAutomationFixture(workspace.ViewModel);
        fixture.Save(1);
        fixture.Save(1, 1);
        File.WriteAllText(Path.Combine(fixture.Root, "corrupt-private-file.json"), "{ broken");
        var first = (await Automate(workspace.ViewModel, "library_datasets", new { limit = 1 })).Result!.Value;
        string hash = first.GetProperty("catalogHash").GetString()!;
        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.Single(first.GetProperty("datasets").EnumerateArray());
        Assert.Equal("invalid_dataset", first.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("corrupt-private-file", first.GetRawText());
        Assert.DoesNotContain("private-list-id", first.GetRawText());
        var second = await Automate(workspace.ViewModel, "library_datasets", new { offset = 1, limit = 1, catalogHash = hash });
        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.GetProperty("datasets")[0].GetProperty("datasetHash").GetString(),
            second.Result!.Value.GetProperty("datasets")[0].GetProperty("datasetHash").GetString());

        fixture.Save(1, 2);
        var stale = await Automate(workspace.ViewModel, "library_datasets", new { offset = 1, catalogHash = hash });
        Assert.False(stale.Success);
        Assert.Equal("library_catalog_changed", stale.ErrorCode);
        Assert.Equal(0, fixture.Provider.Calls);
    });

    [Fact]
    public Task AutomationLibrary_RejectsPathsInvalidPagesAndMissingOrTamperedPins() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        await using var fixture = new LibraryAutomationFixture(workspace.ViewModel);
        HistoricalDatasetInfo saved = fixture.Save(1);
        Assert.Equal("invalid_arguments", (await Automate(workspace.ViewModel, "library_candles", new { datasetHash = "../data.json" })).ErrorCode);
        Assert.Equal("invalid_arguments", (await Automate(workspace.ViewModel, "library_candles", new { datasetHash = saved.DatasetHash, limit = 101 })).ErrorCode);
        Assert.Equal("invalid_arguments", (await Automate(workspace.ViewModel, "library_datasets", new { offset = 1 })).ErrorCode);
        Assert.Equal("invalid_arguments", (await Automate(workspace.ViewModel, "library_datasets", new { path = fixture.Root })).ErrorCode);
        Assert.Equal("library_dataset_unavailable", (await Automate(workspace.ViewModel, "library_candles", new { datasetHash = new string('a', 64) })).ErrorCode);
        File.WriteAllText(Path.Combine(fixture.Root, saved.RelativePath), "{}");
        var invalid = await Automate(workspace.ViewModel, "library_candles", new { datasetHash = saved.DatasetHash });
        Assert.Equal("library_dataset_unavailable", invalid.ErrorCode);
        Assert.DoesNotContain(fixture.Root, invalid.Error!);
        Assert.Equal(0, fixture.Provider.Calls);
    });

    private sealed class LibraryAutomationFixture : IAsyncDisposable
    {
        internal static readonly DateTimeOffset At = new(2026, 9, 4, 13, 30, 0, TimeSpan.Zero);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pricesentinel-library-mcp", Guid.NewGuid().ToString("N"));
        public OfflineLibraryProvider Provider { get; } = new();
        private readonly DataRetentionViewModel _retention;
        public LibraryAutomationFixture(MainViewModel vm)
        {
            var store = new LibraryCollectionStore(new CollectionState
            {
                Settings = new CollectionSettings
                {
                    LibraryRootPath = Root, Lists = [new DownloadList(Guid.NewGuid(), "private list", false,
                        [new DownloadListMember("NFLX")], "private-list-id", "private source")],
                },
            });
            var collector = new MarketDataCollector(store, Provider, path => new JsonMarketDataLibrary(path));
            _retention = new(collector, Provider, Provider, Provider, path => new JsonMarketDataLibrary(path),
                _ => throw new InvalidOperationException("Library reads cannot authenticate."), () => false);
            vm.DataRetention = _retention;
        }
        public HistoricalDatasetInfo Save(int count, int dayOffset = 0)
        {
            DateTimeOffset at = At.AddDays(dayOffset);
            var download = new HistoricalDownload("Robinhood", "public-nflx-instrument", "NFLX", 15, "split", "split", "regular",
                at.AddDays(1), at, at.AddSeconds(count * 15), Enumerable.Range(0, count).Select(index =>
                    new HistoricalCandle(at.AddSeconds(index * 15), at.AddSeconds((index + 1) * 15), at.AddSeconds((index + 1) * 15),
                        80.12345678901234567890123456m, 81m, 79m, 80m, null)).ToArray());
            return new JsonMarketDataLibrary(Root).Save(download)[0];
        }
        public async ValueTask DisposeAsync()
        {
            await _retention.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class LibraryCollectionStore(CollectionState state) : ICollectionStateStore
    {
        public CollectionState Load() => state;
        public void Save(CollectionState value) => state = value;
    }
    private sealed class OfflineLibraryProvider : IMarketHistoryProvider, IPersonalWatchlistSource, IEquityCatalogSource
    {
        public int Calls { get; private set; }
        private Exception Unexpected() { Calls++; return new InvalidOperationException("Offline library read attempted provider access."); }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken) => throw Unexpected();
        public Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken cancellationToken) => throw Unexpected();
        public Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(PersonalWatchlist watchlist, CancellationToken cancellationToken) => throw Unexpected();
        public Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken) => throw Unexpected();
    }
}
