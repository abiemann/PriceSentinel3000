using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData("regular", false)]
    [InlineData("regular", true)]
    [InlineData("extended", false)]
    [InlineData("extended", true)]
    [InlineData("24_5", false)]
    [InlineData("24_5", true)]
    public Task Replay_AllHoursAndLegacySessionFilesRemainLocalForCalendarCheckAndStart(string sessionBounds, bool checkFirst) => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDatasetInfo saved = Assert.Single(files.Library.Save(LibraryDownload(start, 15) with { SessionBounds = sessionBounds }));
        files.Provider.Error = new InvalidOperationException("Complete saved history must not request broker history.");
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.LoadReplayCalendarMonthAsync(start.LocalDateTime);
        Assert.Equal("Disk15", vm.ReplayCalendarDays[DateOnly.FromDateTime(start.LocalDateTime)].Status);
        if (checkFirst)
        {
            await vm.CheckReplayAvailabilityAsync();
            Assert.Equal("Disk15", vm.ReplayAvailabilityStatus);
        }
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() is "completed" or "failed");

        Assert.Contains("Replay completed", vm.StatusMessage);
        Assert.Equal(0, files.Provider.Calls);
        Assert.Equal(0, files.Connections);
        Assert.Equal(0, workspace.Broker.Connections);
        HistoricalDatasetInfo retained = Assert.Single(workspace.Get<LibraryReplayHistoryResult>("_resolvedReplayHistory").Datasets);
        Assert.Equal(saved.DatasetHash, retained.DatasetHash);
        Assert.Equal(sessionBounds, retained.SessionBounds);
        Assert.Equal(saved.DatasetHash, Assert.Single(files.Library.Scan().Datasets).DatasetHash);
    });

    [Fact]
    public Task Replay_CompatibleSessionPiecesUseExactlyTheCheckedUnionWithoutFetching() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        DateTimeOffset start = AvailabilityStart(workspace);
        HistoricalDownload download = LibraryDownload(start, 15);
        string regular = Assert.Single(files.Library.Save(download with { Candles = download.Candles.Take(5).ToArray() })).DatasetHash;
        string overnight = Assert.Single(files.Library.Save(download with
        {
            SessionBounds = "24_5", Candles = download.Candles.Skip(3).ToArray(),
        })).DatasetHash;
        files.Provider.Error = new InvalidOperationException("Compatible saved pieces fully cover the requested range.");
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, start, 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();
        Assert.Equal("Disk15", vm.ReplayAvailabilityStatus);
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() is "completed" or "failed");

        Assert.Contains("Replay completed", vm.StatusMessage);
        Assert.Equal(0, files.Provider.Calls);
        LibraryReplayHistoryResult resolved = workspace.Get<LibraryReplayHistoryResult>("_resolvedReplayHistory");
        Assert.Equal(download.Candles, resolved.Candles);
        Assert.Equal(new[] { regular, overnight }.Order(), resolved.Datasets.Select(d => d.DatasetHash).Order());
        Assert.Equal(new[] { "24_5", "regular" }, resolved.Datasets.Select(d => d.SessionBounds).Order());
    });

    [Fact]
    public Task Replay_BrokerChecksRequestAllHoursIndependentlyOfLegacyCollectionBounds() => host.RunAsync(async () =>
    {
        using var files = new AvailabilityFiles();
        await using var workspace = new TestWorkspace();
        files.Provider.CompleteInterval = 15;
        MainViewModel vm = workspace.ViewModel;
        vm.DataRetention = files.CreateRetention();
        await ConfigureLibraryReplay(vm, AvailabilityStart(workspace), 60, "builtin");

        await vm.CheckReplayAvailabilityAsync();

        Assert.Equal("Broker15", vm.ReplayAvailabilityStatus);
        Assert.Equal("24_5", Assert.Single(files.Provider.Requests).SessionBounds);
    });
}
