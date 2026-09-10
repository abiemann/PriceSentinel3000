using System.Collections.Specialized;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task DownloadCoverage_LibraryRescanAndClockRefreshKeepBothTablesConsistent() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "AAPL", SessionBounds = "24_5", Status = CollectionJobStatus.Partial,
            SavedCoveragePercent = 60.26m,
        });
        MarketDataCollector collector = fixture.ViewModel.Collector;
        CollectionJob job = Assert.Single(collector.State.Jobs);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(job.SessionDate, "24_5").FromUtc;
        fixture.Clock.Now = start.AddSeconds(4343 * 15);
        HistoricalDatasetInfo saved = ClockCoverageDataset(job.SessionDate, start, start.AddSeconds(3471 * 15))
            with { SessionBounds = "24_5" };
        var library = new ScanLibrary(job.LibraryRootPath) { Result = new([saved], []) };
        await using var vm = new DataRetentionViewModel(collector, fixture.Provider, fixture.Provider, fixture.Provider,
            _ => library, _ => Task.CompletedTask, () => false, clock: fixture.Clock);
        DownloadJobViewModel row = Assert.Single(vm.Jobs);

        await vm.ScanLibraryAsync();
        Assert.Equal("79.92%", row.StateText);
        Assert.Equal(Assert.Single(vm.LibraryDays).CoveragePercent, row.StateCoveragePercent);
        Assert.Same(row, Assert.Single(vm.Jobs));
        decimal? persisted = Assert.Single(collector.State.Jobs).SavedCoveragePercent;
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Jobs.CollectionChanged += (_, change) => changes.Add(change.Action);

        // Crossing a candle boundary updates both displays without rescanning files or writing state.
        library.Failure = new InvalidOperationException("Clock refresh must not scan the library.");
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(15);
        vm.RefreshLibraryCoverage();
        Assert.Equal(100m * 3471 / 4344, row.StateCoveragePercent);
        Assert.Equal(Assert.Single(vm.LibraryDays).CoveragePercent, row.StateCoveragePercent);
        Assert.Equal(persisted, Assert.Single(collector.State.Jobs).SavedCoveragePercent);
        Assert.Empty(changes);
        Assert.Same(row, Assert.Single(vm.Jobs));

        library.Failure = null;
        library.Result = new([ClockCoverageDataset(job.SessionDate, start, start.AddSeconds(3480 * 15))
            with { SessionBounds = "24_5" }], []);
        await vm.ScanLibraryAsync();
        Assert.Equal(100m * 3480 / 4344, row.StateCoveragePercent);
        Assert.Equal(Assert.Single(vm.LibraryDays).CoveragePercent, row.StateCoveragePercent);
        Assert.Same(row, Assert.Single(vm.Jobs));
        Assert.Empty(changes);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Equal(0, fixture.ConnectionCalls);
    });

    [Fact]
    public Task DownloadCoverage_ClockRefreshWorksBeforeOpeningLocalLibrary() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob
        {
            Symbol = "AAPL", SessionBounds = "regular", Status = CollectionJobStatus.Partial,
        });
        var vm = fixture.ViewModel;
        CollectionJob job = Assert.Single(vm.Collector.State.Jobs);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(job.SessionDate, "regular").FromUtc;
        fixture.Clock.Now = start.AddMinutes(15);
        var library = new ScanLibrary(job.LibraryRootPath)
        {
            Result = new([ClockCoverageDataset(job.SessionDate, start, start.AddMinutes(15))], []),
        };
        await vm.Collector.ScanLibraryAsync(library);
        vm.RefreshLibraryCoverage();
        DownloadJobViewModel row = Assert.Single(vm.Jobs);
        Assert.Equal(100m, row.StateCoveragePercent);

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(15);
        vm.RefreshLibraryCoverage();
        Assert.Equal(100m * 60 / 61, row.StateCoveragePercent);
        Assert.Empty(vm.Datasets);
        Assert.Empty(vm.LibraryDays);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
    });
}
