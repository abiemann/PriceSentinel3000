using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task LocalLibraryCoverage_ClockRefreshUsesCompletedCandlesAndPreservesGridSelection() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        var today = new DateOnly(2026, 9, 9);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(today, "regular").FromUtc;
        fixture.Clock.Now = start.AddMinutes(15);
        vm.Datasets.Add(ClockCoverageDataset(today, start, start.AddMinutes(15)));
        DateOnly yesterday = today.AddDays(-1);
        var oldSession = CollectionSchedule.GetSessionWindow(yesterday, "regular");
        vm.Datasets.Add(ClockCoverageDataset(yesterday, oldSession.FromUtc, oldSession.ThroughUtc));
        foreach (LibraryDaySummary day in LibraryDaySummary.Create(vm.Datasets, fixture.Clock.Now)) vm.LibraryDays.Add(day);

        var dialog = CreateLocalLayoutDialog(vm, 1080, 790);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            LibraryDaySummary initial = vm.LibraryDays.Single(day => day.TradingDate == today);
            LibraryDaySummary historical = vm.LibraryDays.Single(day => day.TradingDate == yesterday);
            grid.SelectedItem = initial;
            SortDescription[] sorting = vm.VisibleLibraryDays.SortDescriptions.ToArray();
            vm.RefreshLibraryCoverage();
            Assert.Equal(100m, initial.CoveragePercent);

            fixture.Clock.Now = start.AddMinutes(15).AddSeconds(14);
            vm.RefreshLibraryCoverage();
            Assert.Same(initial, vm.LibraryDays.Single(day => day.TradingDate == today));

            fixture.Clock.Now = start.AddMinutes(15).AddSeconds(15);
            vm.RefreshLibraryCoverage();
            await SettleLocalLayout(dialog);
            LibraryDaySummary refreshed = vm.LibraryDays.Single(day => day.TradingDate == today);
            Assert.Equal(100m * 60 / 61, refreshed.CoveragePercent);
            Assert.Equal(60L, refreshed.CandleCount);
            Assert.Same(refreshed, grid.SelectedItem);
            Assert.Same(refreshed, vm.SelectedLibraryDay);
            Assert.Same(historical, vm.LibraryDays.Single(day => day.TradingDate == yesterday));
            Assert.Equal(sorting, vm.VisibleLibraryDays.SortDescriptions.ToArray());
            Assert.Equal(2, vm.Datasets.Count);
            Assert.Equal(0, fixture.Provider.DownloadCalls);
            Assert.Equal(0, fixture.ConnectionCalls);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task LocalLibraryCoverage_FirstCompletedCandleReplacesUnknownPercentage() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        var today = new DateOnly(2026, 9, 9);
        DateTimeOffset start = CollectionSchedule.GetSessionWindow(today, "regular").FromUtc;
        fixture.Clock.Now = start;
        vm.Datasets.Add(ClockCoverageDataset(today, start, start.AddSeconds(15)));
        foreach (LibraryDaySummary day in LibraryDaySummary.Create(vm.Datasets, fixture.Clock.Now)) vm.LibraryDays.Add(day);
        Assert.Null(Assert.Single(vm.LibraryDays).CoveragePercent);

        vm.RefreshLibraryCoverage();
        fixture.Clock.Now = start.AddSeconds(15);
        vm.RefreshLibraryCoverage();

        Assert.Equal(100m, Assert.Single(vm.LibraryDays).CoveragePercent);
        Assert.Equal(1L, Assert.Single(vm.LibraryDays).CandleCount);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
        Assert.Equal(0, fixture.ConnectionCalls);
    });

    private static HistoricalDatasetInfo ClockCoverageDataset(DateOnly day, DateTimeOffset from, DateTimeOffset through)
    {
        int count = (int)((through - from).TotalSeconds / 15);
        return new(day.ToString("yyyyMMdd").PadRight(64, '0'), $"{day:yyyy-MM-dd}.json", "Robinhood",
            "aapl", "AAPL", day, 15, "split", "robinhood-split-unversioned", "regular", through,
            new(from, through, from, through, count, count, true, true, []));
    }
}
