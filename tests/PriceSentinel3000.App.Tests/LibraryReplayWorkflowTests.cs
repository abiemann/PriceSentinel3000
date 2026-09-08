using System.Globalization;
using System.IO;
using System.Text.Json;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task LibraryReplay_PinnedOfflineSessionPreservesPricesNullVolumeAndRetainedHashes() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace(new TestScriptCatalog
        {
            Source = "def mean = Average(close, 2); AddOrder(OrderType.BUY_TO_OPEN, close > mean);",
        });
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        HistoricalDownload download = LibraryDownload(start, 30);
        string hash = Assert.Single(files.Library.Save(download)).DatasetHash;
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayPinnedHashes = hash;
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 30);
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 2 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean() || value.GetProperty("operationState").GetString() == "failed");
        Assert.True(vm.IsReplayPaused, vm.StatusMessage);

        Assert.Equal("LOCAL LIBRARY", vm.MarketDataStatus);
        Assert.Equal("30 SEC CANDLES", vm.DataResolutionLabel);
        Assert.Contains("local data library", vm.DataResolutionDescription);
        Assert.Contains(hash, vm.DataResolutionDescription);
        Assert.Contains("4/4 candles (complete)", vm.DataResolutionDescription);
        JsonElement status = (await Automate(vm, "status")).Result!.Value;
        JsonElement provenance = status.GetProperty("replayHistory");
        Assert.Equal("pinned-library", provenance.GetProperty("Source").GetString());
        Assert.Equal(hash, Assert.Single(provenance.GetProperty("DatasetHashes").EnumerateArray()).GetString());
        Assert.Equal("robinhood-split-unversioned", provenance.GetProperty("Datasets")[0].GetProperty("AdjustmentBasis").GetString());
        JsonElement sources = (await Automate(vm, "candles", new { kind = "source" })).Result!.Value.GetProperty("records");
        Assert.Equal(JsonValueKind.Null, sources[0].GetProperty("volume").ValueKind);
        Assert.Equal(download.Candles[0].High, sources[0].GetProperty("high").GetDecimal());
        Assert.Equal(start.AddSeconds(30), sources[0].GetProperty("availableAtUtc").GetDateTimeOffset());
        Assert.Equal(0m, sources[0].GetProperty("bid").GetDecimal());
        Assert.Equal(0m, sources[0].GetProperty("ask").GetDecimal());
        JsonElement strategyBars = (await Automate(vm, "candles", new { kind = "strategy" })).Result!.Value.GetProperty("records");
        Assert.Equal(JsonValueKind.Null, strategyBars[0].GetProperty("volume").ValueKind);
        JsonElement indicators = (await Automate(vm, "indicators")).Result!.Value;
        Assert.Equal(JsonValueKind.Null, indicators.GetProperty("latestEvaluation").GetProperty("latestBar").GetProperty("volume").ValueKind);
        Assert.Equal(0, workspace.Broker.Connections);

        Assert.True((await Automate(vm, "run_to_end")).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        vm.DataRetention.ReplayPinnedHashes = "";
        vm.DataRetention.ReplayUseLatestRevision = true;
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal(provenance.GetRawText(), results.GetProperty("replayHistory").GetRawText());
        Assert.Equal("LOCAL LIBRARY", vm.MarketDataStatus);
        Assert.Equal("HISTORY LOADED", vm.MarketDataStateLabel);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal(0, files.Connections);
    });

    [Fact]
    public Task LibraryReplay_OnlineWithCompleteLocalHistoryNeedsNoLogin() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() is "completed" or "failed");
        Assert.Contains("Replay completed", vm.StatusMessage);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal("LOCAL LIBRARY", vm.MarketDataStatus);
    });

    [Fact]
    public Task LibraryReplay_MissingPinFailsBeforeAnySessionOrLogin() => host.RunAsync(async () =>
    {
        using var files = new ReplayLibraryFiles();
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset start = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        files.Library.Save(LibraryDownload(start, 15));
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayPinnedHashes = new string('a', 64);
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "failed");
        Assert.False(vm.IsSessionRunning);
        Assert.Contains("pinned dataset", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal(JsonValueKind.Null, (await Automate(vm, "results")).Result!.Value.GetProperty("sessionId").ValueKind);
    });

    private static async Task ConfigureLibraryReplay(MainViewModel vm, DateTimeOffset start, int scriptInterval,
        string strategy = "test.thinkscript")
    {
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new
            {
                strategyId = strategy, scriptBarIntervalSeconds = scriptInterval,
                replayDate = start.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                replayTime = start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                replayEndTime = start.AddMinutes(2).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            },
        })).Success);
    }

    private static HistoricalDownload LibraryDownload(DateTimeOffset start, int interval) => new("Robinhood", "test-sofi", "SOFI",
        interval, "split", "robinhood-split-unversioned", "regular", start.AddDays(1), start, start.AddMinutes(2),
        Enumerable.Range(0, 120 / interval).Select(index =>
        {
            DateTimeOffset at = start.AddSeconds(index * interval);
            decimal price = 10m + index;
            return new HistoricalCandle(at, at.AddSeconds(interval), at.AddSeconds(interval), price,
                price + 0.1234567890123456789m, price - 1m, price, null);
        }).ToArray());

    private sealed class ReplayLibraryFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-library-ui-tests", Guid.NewGuid().ToString("N"));
        public JsonMarketDataLibrary Library => new(_root);
        public UnusedLibraryProvider Provider { get; } = new();
        public int Connections { get; private set; }
        public DataRetentionViewModel CreateRetention()
        {
            var collector = new MarketDataCollector(new MemoryCollectionStore(_root), Provider, path => new JsonMarketDataLibrary(path));
            return new(collector, Provider, Provider, Provider, path => new JsonMarketDataLibrary(path), _ =>
            {
                Connections++;
                throw new InvalidOperationException("Local Replay unexpectedly requested login.");
            }, () => false);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class MemoryCollectionStore(string root) : ICollectionStateStore
    {
        public CollectionState Load() => new() { Settings = new() { LibraryRootPath = root } };
        public void Save(CollectionState state) => throw new InvalidOperationException("Replay must not modify collection settings.");
    }

    private sealed class UnusedLibraryProvider : IMarketHistoryProvider, IPersonalWatchlistSource, IEquityCatalogSource
    {
        public int Calls { get; private set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Local Replay unexpectedly requested provider history.");
        }
        public Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(PersonalWatchlist watchlist, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(IReadOnlyList<string> symbols, CancellationToken token) => throw new NotSupportedException();
    }
}
