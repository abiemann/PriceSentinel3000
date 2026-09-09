using System.Windows.Controls;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task LocalLibrary_DailyRowsCombineRevisionsAndKeepSelectionAfterRescan(int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        fixture.Clock.Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var date = new DateOnly(2026, 9, 8);
        DateTimeOffset open = CollectionSchedule.GetSessionWindow(date).FromUtc;
        var library = new ScanLibrary(fixture.LibraryRoot)
        {
            Result = new([
                DailyRowDataset("AAPL", date, open, open.AddHours(2), "regular", 'a'),
                DailyRowDataset("AAPL", date, open, open.AddHours(3), "regular", 'b'),
                DailyRowDataset("AAPL", date, open.AddHours(2.5), open.AddHours(6.5), "24_5", 'c'),
                DailyRowDataset("AMD", date, open, open.AddMinutes(1), "regular", 'd'),
                DailyRowDataset("AAPL", new(2026, 9, 4), open.AddDays(-4), open.AddDays(-4).AddMinutes(1), "regular", 'e'),
            ], []),
        };
        await using DataRetentionViewModel vm = CreateScanViewModel(fixture, library);
        await vm.ScanLibraryAsync();
        Assert.Equal(5, vm.Datasets.Count);
        Assert.Equal(3, vm.LibraryDays.Count);
        Assert.Contains("3 daily entries from 5 saved files", vm.Status);
        Assert.Equal(new[] { ("AAPL", date), ("AMD", date), ("AAPL", date.AddDays(-4)) },
            vm.LibraryDays.Select(day => (day.Symbol, day.TradingDate)));
        LibraryDaySummary aapl = vm.LibraryDays[0];
        Assert.Equal(1560, aapl.CandleCount);
        Assert.Equal(100m * 1560 / 5760, aapl.CoveragePercent);
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            Assert.Equal(3, grid.Items.Count);
            var coverage = Assert.IsType<DataGridTextColumn>(Assert.Single(grid.Columns,
                column => Equals(column.Header, "Day coverage")));
            var cell = Assert.IsType<TextBlock>(coverage.GetCellContent(aapl));
            Assert.Equal("27.08%", cell.Text);
            Assert.Contains("1,560", Assert.IsType<string>(cell.ToolTip));
            Assert.Contains("5,760", Assert.IsType<string>(cell.ToolTip));
            grid.SelectedItem = aapl;
            await SettleLocalLayout(dialog);
            CaptureLocalLayout(dialog, $"local-library-daily-{width}x{height}.png");

            await vm.ScanLibraryAsync();
            await SettleLocalLayout(dialog);
            Assert.Equal(3, grid.Items.Count);
            Assert.Same(vm.LibraryDays[0], grid.SelectedItem);
            Assert.Same(vm.LibraryDays[0], vm.SelectedLibraryDay);
            Assert.Equal(5, vm.Datasets.Count);
            Assert.Equal(0, fixture.Provider.Calls);
        }
        finally { dialog.Close(); }
    });

    private static HistoricalDatasetInfo DailyRowDataset(string symbol, DateOnly date, DateTimeOffset from,
        DateTimeOffset through, string session, char hash)
    {
        int count = (int)((through - from).TotalSeconds / 15);
        return new(new string(hash, 64), symbol + hash + ".json", "Robinhood", symbol + "-id", symbol,
            date, 15, "split", "unversioned", session, through,
            new(from, through, from, through, count, count, true, true, []));
    }
}