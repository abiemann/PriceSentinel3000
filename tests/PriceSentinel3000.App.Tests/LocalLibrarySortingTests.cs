using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task LocalLibrarySorting_DefaultDateThenEquityShowsVisiblePriorities(int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryDaySummary[] original = AddLocalSortingRows(vm);
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");

            Assert.Equal(new[] { original[3], original[2], original[1], original[0] }, grid.Items.Cast<LibraryDaySummary>());
            Assert.Equal(original, vm.LibraryDays);
            AssertLocalSortDescriptions(grid,
                (nameof(LibraryDaySummary.TradingDate), ListSortDirection.Descending),
                (nameof(LibraryDaySummary.Symbol), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Descending, "↓ 1");
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 2");
            foreach (string name in new[] { "Day coverage", "Candles", "Provider" })
                AssertLocalSortIndicator(grid, name, null, "↕");
            AssertInsideWindow(dialog, grid);
            CaptureLocalLayout(dialog, $"local-library-sort-{width}x{height}-default.png");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task LocalLibrarySorting_SecondaryDateCanReverseWithoutChangingPrimaryEquity() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryDaySummary[] original = AddLocalSortingRows(vm);
        var dialog = CreateLocalLayoutDialog(vm, 1080, 790);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");

            await SetLocalPrimaryAscending(grid, "Equity");
            await InvokeLocalSecondarySort(grid, "Date");
            AssertLocalSortDescriptions(grid,
                (nameof(LibraryDaySummary.Symbol), ListSortDirection.Ascending),
                (nameof(LibraryDaySummary.TradingDate), ListSortDirection.Ascending));
            Assert.Equal(new[] { original[1], original[3], original[0], original[2] }, grid.Items.Cast<LibraryDaySummary>());
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 2");

            await InvokeLocalSecondarySort(grid, "Date");
            AssertLocalSortDescriptions(grid,
                (nameof(LibraryDaySummary.Symbol), ListSortDirection.Ascending),
                (nameof(LibraryDaySummary.TradingDate), ListSortDirection.Descending));
            Assert.Equal(new[] { original[3], original[1], original[2], original[0] }, grid.Items.Cast<LibraryDaySummary>());
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Descending, "↓ 2");
            CaptureLocalLayout(dialog, "local-library-sort-equity-then-date.png");

            await InvokeDownloadSortHeader(grid, "Date");
            AssertLocalSortDescriptions(grid, (nameof(LibraryDaySummary.TradingDate), ListSortDirection.Ascending));
            Assert.Equal(new[] { new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8) },
                grid.Items.Cast<LibraryDaySummary>().Select(row => row.TradingDate));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Equity", null, "↕");
            Assert.Equal(original, vm.LibraryDays);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task LocalLibrarySorting_CoverageAndCandleCountsUseNumericValues() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        var date = new DateOnly(2026, 9, 8);
        vm.LibraryDays.Add(new("HIGH", date, "15", "Robinhood", 100, 100m, "100 candles saved."));
        vm.LibraryDays.Add(new("LOW", date, "15", "Robinhood", 9, 9m, "9 candles saved."));
        vm.LibraryDays.Add(new("MID", date, "15", "Robinhood", 80, 80m, "80 candles saved."));
        var dialog = CreateLocalLayoutDialog(vm, 860, 620);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");

            await InvokeDownloadSortHeader(grid, "Day coverage");
            Assert.Equal(new decimal?[] { 9m, 80m, 100m }, grid.Items.Cast<LibraryDaySummary>().Select(row => row.CoveragePercent));
            AssertLocalSortIndicator(grid, "Day coverage", ListSortDirection.Ascending, "↑ 1");
            await InvokeDownloadSortHeader(grid, "Day coverage");
            Assert.Equal(new decimal?[] { 100m, 80m, 9m }, grid.Items.Cast<LibraryDaySummary>().Select(row => row.CoveragePercent));
            AssertLocalSortIndicator(grid, "Day coverage", ListSortDirection.Descending, "↓ 1");

            await InvokeDownloadSortHeader(grid, "Candles");
            Assert.Equal(new long?[] { 9, 80, 100 }, grid.Items.Cast<LibraryDaySummary>().Select(row => row.CandleCount));
            AssertLocalSortDescriptions(grid, (nameof(LibraryDaySummary.CandleCount), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "Candles", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Day coverage", null, "↕");
            CaptureLocalLayout(dialog, "local-library-sort-numeric.png");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task LocalLibrarySorting_RescanAndReopeningKeepMultiSortAndSelectedDay() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        var oldDate = new DateOnly(2026, 9, 3);
        var newDate = new DateOnly(2026, 9, 8);
        HistoricalDatasetInfo Dataset(string symbol, DateOnly date, char hash)
        {
            DateTimeOffset open = CollectionSchedule.GetSessionWindow(date).FromUtc;
            return DailyRowDataset(symbol, date, open, open.AddMinutes(1), "regular", hash);
        }
        var library = new ScanLibrary(fixture.LibraryRoot)
        {
            Result = new([
                Dataset("MSFT", oldDate, 'a'), Dataset("AAPL", newDate, 'b'),
                Dataset("AAPL", oldDate, 'c'), Dataset("MSFT", newDate, 'd'),
            ], []),
        };
        await using DataRetentionViewModel vm = CreateScanViewModel(fixture, library);
        await vm.ScanLibraryAsync();
        DataRetentionDialog? dialog = CreateLocalLayoutDialog(vm, 1080, 790);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            await SetLocalPrimaryAscending(grid, "Equity");
            await InvokeLocalSecondarySort(grid, "Date");
            LibraryDaySummary selected = Assert.Single(vm.LibraryDays, row => row.Symbol == "MSFT" && row.TradingDate == oldDate);
            grid.SelectedItem = selected;
            await SettleLocalLayout(dialog);

            library.Result = new(library.Result.Datasets.Append(Dataset("AMD", newDate, 'e')).ToArray(), []);
            await vm.ScanLibraryAsync();
            await SettleLocalLayout(dialog);
            Assert.Equal(new[] { ("AAPL", oldDate), ("AAPL", newDate), ("AMD", newDate), ("MSFT", oldDate), ("MSFT", newDate) },
                grid.Items.Cast<LibraryDaySummary>().Select(row => (row.Symbol, row.TradingDate)));
            AssertLocalSortDescriptions(grid,
                (nameof(LibraryDaySummary.Symbol), ListSortDirection.Ascending),
                (nameof(LibraryDaySummary.TradingDate), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 2");
            LibraryDaySummary rescannedSelection = Assert.IsType<LibraryDaySummary>(grid.SelectedItem);
            Assert.Equal(("MSFT", oldDate), (rescannedSelection.Symbol, rescannedSelection.TradingDate));
            Assert.Same(vm.SelectedLibraryDay, rescannedSelection);
            Assert.NotSame(selected, rescannedSelection);

            dialog.Close();
            dialog = CreateLocalLayoutDialog(vm, 1080, 790);
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            Assert.Equal(new[] { ("AAPL", oldDate), ("AAPL", newDate), ("AMD", newDate), ("MSFT", oldDate), ("MSFT", newDate) },
                grid.Items.Cast<LibraryDaySummary>().Select(row => (row.Symbol, row.TradingDate)));
            AssertLocalSortDescriptions(grid,
                (nameof(LibraryDaySummary.Symbol), ListSortDirection.Ascending),
                (nameof(LibraryDaySummary.TradingDate), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 2");
            Assert.Same(rescannedSelection, grid.SelectedItem);
            Assert.Equal(0, fixture.Provider.Calls);
        }
        finally { dialog?.Close(); }
    });

    private static LibraryDaySummary[] AddLocalSortingRows(DataRetentionViewModel vm)
    {
        LibraryDaySummary[] rows =
        [
            new("MSFT", new(2026, 9, 3), "15", "Robinhood", 100, 10m, "Saved data."),
            new("AAPL", new(2026, 9, 3), "15", "Robinhood", 200, 20m, "Saved data."),
            new("MSFT", new(2026, 9, 8), "15", "Robinhood", 300, 30m, "Saved data."),
            new("AAPL", new(2026, 9, 8), "15", "Robinhood", 400, 40m, "Saved data."),
        ];
        foreach (LibraryDaySummary row in rows) vm.LibraryDays.Add(row);
        return rows;
    }

    private static void AssertLocalSortDescriptions(DataGrid grid, params (string Property, ListSortDirection Direction)[] expected) =>
        Assert.Equal(expected, grid.Items.SortDescriptions.Select(sort => (sort.PropertyName, sort.Direction)));

    private static void AssertLocalSortIndicator(DataGrid grid, string name, ListSortDirection? direction, string text)
    {
        DataGridColumnHeader header = DownloadSortingHeader(grid, name);
        Assert.True(header.Column.CanUserSort);
        Assert.Equal(direction, header.Column.SortDirection);
        var indicator = Assert.IsType<TextBlock>(header.Template.FindName("LibrarySortIndicator", header));
        Assert.True(indicator.IsVisible);
        Assert.Equal(text, indicator.Text);
        Assert.Contains("Shift", Assert.IsType<string>(header.ToolTip), StringComparison.OrdinalIgnoreCase);
        Assert.True(indicator.ActualWidth > 0);
        Point origin = indicator.TransformToAncestor(header).Transform(new Point());
        Assert.True(origin.X >= 0 && origin.X + indicator.ActualWidth <= header.ActualWidth + 1,
            $"The {name} sort indicator is clipped: {origin.X:0.##} + {indicator.ActualWidth:0.##} > {header.ActualWidth:0.##}.");
    }

    private static async Task SetLocalPrimaryAscending(DataGrid grid, string name)
    {
        await InvokeDownloadSortHeader(grid, name);
        if (DownloadSortingHeader(grid, name).Column.SortDirection != ListSortDirection.Ascending)
            await InvokeDownloadSortHeader(grid, name);
        Assert.Single(grid.Items.SortDescriptions);
    }

    private static async Task InvokeLocalSecondarySort(DataGrid grid, string name)
    {
        // Exercise WPF's same sort branch used by Shift-click, without changing the user's keyboard state.
        MethodInfo sort = typeof(DataGrid).GetMethod("DefaultSort", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sort.Invoke(grid, [DownloadSortingHeader(grid, name).Column, false]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        grid.UpdateLayout();
    }
}
