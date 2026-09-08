using System.Globalization;
using System.IO;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ReplayAvailability_CompleteDiskFineIsDarkGreenWithoutNetworkOrSession() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        string hash = Assert.Single(files.Library.Save(LibraryDownload(start, 15))).DatasetHash;
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();

        Assert.Equal("Disk15", vm.ReplayAvailabilityStatus);
        Assert.Contains("8/8", vm.ReplayAvailabilityText);
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(hash, Assert.Single(files.Library.Scan().Datasets).DatasetHash);
        Assert.True(vm.StartSessionCommand.CanExecute(null));
    });

    [Fact]
    public Task ReplayAvailability_BrokerFineIsLightGreen_ArchivesOnlyAtStartWithoutAnotherRequest() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Provider.CompleteInterval = 15;
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();

        Assert.Equal("Broker15", vm.ReplayAvailabilityStatus);
        Assert.Equal(1, files.Provider.Calls);
        Assert.False(Directory.Exists(files.Root));
        Assert.False(vm.IsSessionRunning);
        files.Provider.Error = new IOException("Broker unavailable after check");
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() is "completed" or "failed");

        Assert.Contains("Replay completed", vm.StatusMessage);
        Assert.Equal(1, files.Provider.Calls);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.Equal(15, Assert.Single(files.Library.Scan().Datasets).SourceIntervalSeconds);
    });

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public Task ReplayAvailability_CompleteCoarseDiskCoverageAvoidsFineBrokerRequests(int interval) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload fine = LibraryDownload(start, 15);
        files.Library.Save(fine with { Candles = [fine.Candles[0]] });
        files.Library.Save(LibraryDownload(start, interval));
        files.Provider.Error = new InvalidOperationException("Complete local coverage must not request broker history.");
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();

        Assert.Equal(interval <= 60 ? "Coarse60" : "Coarse120", vm.ReplayAvailabilityStatus);
        Assert.Contains($"{interval}-second replay data on disk", vm.ReplayAvailabilityText);
        Assert.Contains("Ready for START", vm.ReplayAvailabilityText);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Theory]
    [InlineData(30, false, "Coarse60")]
    [InlineData(60, false, "Coarse60")]
    [InlineData(120, false, "Coarse120")]
    [InlineData(15, true, "Partial")]
    public Task ReplayAvailability_ActualResolutionAndPartialCoverageHaveTruthfulPalette(int interval, bool partial, string status) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload download = LibraryDownload(start, interval);
        files.Library.Save(partial ? download with { Candles = [download.Candles[0]] } : download);
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();

        Assert.Equal(status, vm.ReplayAvailabilityStatus);
        Assert.Contains($"{interval}-second", vm.ReplayAvailabilityText);
        Assert.Equal(0, files.Provider.Calls);
    });

    [Theory]
    [InlineData("symbol")]
    [InlineData("date")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("root")]
    [InlineData("bounds")]
    [InlineData("pins")]
    [InlineData("offline")]
    [InlineData("revision")]
    public Task ReplayAvailability_ScopeChangesInvalidatePreparedSelection(string field) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Library.Save(LibraryDownload(start, 15));
        MainViewModel vm = workspace.ViewModel;
        DataRetentionViewModel retention = files.CreateRetention();
        vm.DataRetention = retention;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Disk15", vm.ReplayAvailabilityStatus);

        switch (field)
        {
            case "symbol": vm.Symbol = "MSFT"; break;
            case "date": vm.ReplayDate = start.AddDays(-1).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case "start": vm.ReplayTime = start.AddMinutes(-1).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture); break;
            case "end": vm.ReplayEndTime = start.AddMinutes(3).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture); break;
            case "root":
                retention.LibraryRootPath = Path.Combine(files.Root, "other");
                Assert.Equal("Disk15", vm.ReplayAvailabilityStatus); // Unsaved draft is not the active library.
                await retention.SaveScheduleAsync();
                break;
            case "bounds": retention.SessionBounds = "extended"; await retention.SaveScheduleAsync(); break;
            case "pins": retention.ReplayPinnedHashes = new string('a', 64); break;
            case "offline": retention.ReplayOfflineOnly = true; break;
            case "revision": retention.ReplayUseLatestRevision = true; break;
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
        Assert.False(vm.IsCheckingReplayAvailability);
        Assert.Null(workspace.Get<object?>("_preparedReplay"));
        Assert.Equal(0, files.Provider.Calls);
    });

    [Fact]
    public Task ReplayAvailability_SlowOldResponseCannotPaintOrPrepareNewSymbol() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Provider.CompleteInterval = 15;
        files.Provider.HoldResponse = true;
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        Task checking = vm.CheckReplayAvailabilityAsync();
        await files.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsCheckingReplayAvailability);
        Assert.False(vm.StartSessionCommand.CanExecute(null));
        vm.Symbol = "MSFT";
        files.Provider.Release.TrySetResult();
        await checking;

        Assert.Equal("MSFT", vm.Symbol);
        Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
        Assert.False(vm.IsCheckingReplayAvailability);
        Assert.Null(workspace.Get<object?>("_preparedReplay"));
        Assert.False(Directory.Exists(files.Root));
        Assert.Empty(vm.ReplayCalendarDays);
    });

    [Fact]
    public Task ReplayAvailability_LeavingReplayDiscardsPendingCheck() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        files.Provider.CompleteInterval = 15;
        files.Provider.HoldResponse = true;
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, AvailabilityStart(workspace), 60, "builtin");

        Task checking = vm.CheckReplayAvailabilityAsync();
        await files.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.RequestModeSelection(TradingMode.Off));
        files.Provider.Release.TrySetResult();
        await checking;

        Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
        Assert.False(vm.IsCheckingReplayAvailability);
        Assert.Null(workspace.Get<object?>("_preparedReplay"));
        Assert.False(Directory.Exists(files.Root));
    });

    [Fact]
    public Task ReplayCalendar_MonthScanUsesDiskOnly_AndPreservesCheckedDayWhenDateChanges() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Library.Save(LibraryDownload(start, 15));
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        DateOnly localDay = DateOnly.FromDateTime(start.LocalDateTime);

        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Disk15", vm.ReplayCalendarDays[localDay].Status);
        Assert.Equal("Unknown", vm.ReplayCalendarDays[localDay.AddDays(-1)].Status);
        Assert.Equal(0, files.Provider.Calls);

        files.Provider.CompleteInterval = 15;
        vm.ReplayDate = localDay.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Broker15", vm.ReplayAvailabilityStatus);
        vm.ReplayDate = localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Disk15", vm.ReplayCalendarDays[localDay].Status);
        Assert.Equal("Broker15", vm.ReplayCalendarDays[localDay.AddDays(-1)].Status);
        Assert.Equal(1, files.Provider.Calls);
        Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
    });

    [Fact]
    public Task ReplayCalendar_UsesSelectedTimeRangeRatherThanWholeDailyFileCompleteness() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload download = LibraryDownload(start, 15);
        files.Library.Save(download with { Candles = download.Candles.Take(4).ToArray() });
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        DateOnly day = DateOnly.FromDateTime(start.LocalDateTime);
        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);
        Assert.Equal("Partial", vm.ReplayCalendarDays[day].Status);

        vm.ReplayEndTime = start.AddMinutes(1).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Disk15", vm.ReplayCalendarDays[day].Status);
        Assert.Equal(0, files.Provider.Calls);
    });

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(120)]
    public Task ReplayCalendar_DeletedCheckedFilesAreNotShownAsStillAvailable(int interval) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDatasetInfo dataset = Assert.Single(files.Library.Save(LibraryDownload(start, interval)));
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        await vm.CheckReplayAvailabilityAsync();
        Assert.Contains(vm.ReplayAvailabilityStatus, new[] { "Disk15", "Coarse60", "Coarse120" });
        File.Delete(Path.Combine(files.Root, dataset.RelativePath));

        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Unknown", vm.ReplayCalendarDays[DateOnly.FromDateTime(start.LocalDateTime)].Status);
        Assert.Equal(0, files.Provider.Calls);
    });

    [Fact]
    public Task ReplayCalendar_TwoMinuteSourceCannotMarkOddMinuteWindowAsComplete() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Library.Save(LibraryDownload(start, 120));
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayOfflineOnly = true;
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        vm.ReplayTime = start.AddMinutes(1).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Unknown", vm.ReplayCalendarDays[DateOnly.FromDateTime(start.LocalDateTime)].Status);
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Unavailable", vm.ReplayAvailabilityStatus);
        Assert.Equal(0, files.Provider.Calls);
    });

    [Fact]
    public Task ReplayCalendar_BrokerEvidenceExpiresWithoutAutomaticNetworkRefresh() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Provider.CompleteInterval = 15;
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Broker15", vm.ReplayAvailabilityStatus);

        workspace.Clock.Now = workspace.Clock.Now.AddMinutes(6);
        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Unknown", vm.ReplayCalendarDays[DateOnly.FromDateTime(start.LocalDateTime)].Status);
        Assert.Equal(1, files.Provider.Calls);
        Assert.False(Directory.Exists(files.Root));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReplayCalendar_RevisionConflictIsUnknownEvenWhenCoarseDataOrLatestPolicyExists(bool pinBoth) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        string first = Assert.Single(files.Library.Save(LibraryDownload(start, 15))).DatasetHash;
        string revised = Assert.Single(files.Library.Save(LibraryDownload(start, 15) with
        {
            FetchedAtUtc = start.AddDays(2),
            Candles = LibraryDownload(start, 15).Candles.Select(item => item with { Close = item.Close + 0.1m }).ToArray(),
        })).DatasetHash;
        files.Library.Save(LibraryDownload(start, 60));
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayOfflineOnly = true;
        if (pinBoth)
        {
            vm.DataRetention.ReplayPinnedHashes = first + " " + revised;
            vm.DataRetention.ReplayUseLatestRevision = true;
        }
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);

        Assert.Equal("Unknown", vm.ReplayCalendarDays[DateOnly.FromDateTime(start.LocalDateTime)].Status);
        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
        Assert.Equal(0, files.Provider.Calls);
    });

    [Fact]
    public Task ReplayAvailability_MissingPinsRemainUnknown_WithoutBrokerFallback() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        files.Library.Save(LibraryDownload(start, 15));
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        vm.DataRetention.ReplayPinnedHashes = new string('a', 64);
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Unknown", vm.ReplayAvailabilityStatus);
        Assert.Contains("pinned dataset", vm.ReplayAvailabilityText, StringComparison.OrdinalIgnoreCase);
        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);
        Assert.Equal("Unknown", vm.ReplayCalendarDays[DateOnly.FromDateTime(start.LocalDateTime)].Status);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    private static DateTimeOffset AvailabilityStart(TestWorkspace workspace) =>
        PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(2));

    private sealed class AvailabilityFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PriceSentinel-availability-ui-tests", Guid.NewGuid().ToString("N"));
        public JsonMarketDataLibrary Library => new(Root);
        public AvailabilityProvider Provider { get; } = new();
        public int Connections { get; private set; }
        public bool AllowConnections { get; set; }

        public DataRetentionViewModel CreateRetention()
        {
            var collector = new MarketDataCollector(new AvailabilityStore(Root), Provider, path => new JsonMarketDataLibrary(path));
            return new(collector, Provider, Provider, Provider, path => new JsonMarketDataLibrary(path), _ =>
            {
                Connections++;
                if (!AllowConnections)
                    throw new InvalidOperationException("Checking or starting prepared Replay must not request login.");
                return Task.CompletedTask;
            }, () => false);
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class AvailabilityStore(string root) : ICollectionStateStore
    {
        private CollectionState _state = new() { Settings = new() { LibraryRootPath = root } };
        public CollectionState Load() => _state;
        public void Save(CollectionState state) => _state = state;
    }

    private sealed class AvailabilityProvider : IMarketHistoryProvider, IPersonalWatchlistSource, IEquityCatalogSource
    {
        public int Calls { get; private set; }
        public List<HistoricalDataRequest> Requests { get; } = [];
        public int CompleteInterval { get; set; }
        public Exception? Error { get; set; }
        public bool HoldResponse { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken token)
        {
            Calls++;
            Requests.Add(request);
            Entered.TrySetResult();
            if (HoldResponse) await Release.Task; // Deliberately ignores cancellation to test stale-result rejection.
            if (Error is not null) throw Error;
            HistoricalDownload download = LibraryDownload(request.FromUtc, request.SourceIntervalSeconds) with
            {
                Symbol = request.Symbol, SessionBounds = request.SessionBounds, AdjustmentPolicy = request.AdjustmentPolicy,
                RequestedThroughUtc = request.ThroughUtc,
                Candles = Enumerable.Range(0, (int)((request.ThroughUtc - request.FromUtc).TotalSeconds / request.SourceIntervalSeconds))
                    .Select(index =>
                    {
                        DateTimeOffset at = request.FromUtc.AddSeconds(index * request.SourceIntervalSeconds);
                        decimal price = 10m + index;
                        return new HistoricalCandle(at, at.AddSeconds(request.SourceIntervalSeconds), at.AddSeconds(request.SourceIntervalSeconds),
                            price, price + 0.1234567890123456789m, price - 1m, price, null);
                    }).ToArray(),
            };
            return request.SourceIntervalSeconds == CompleteInterval ? download : download with { Candles = [] };
        }

        public Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(PersonalWatchlist list, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(IReadOnlyList<string> symbols, CancellationToken token) => throw new NotSupportedException();
    }
}
