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
    public Task DownloadSorting_DefaultDateOrderAndIndicatorLeaveTheQueueUnchanged(int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadJobViewModel[] original = AddDownloadSortingRows(vm);
        var dialog = new DataRetentionDialog { DataContext = vm, Width = width, Height = height, ShowActivated = false };
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");

            Assert.Equal(new[] { original[2], original[1], original[0] }, grid.Items.Cast<DownloadJobViewModel>());
            Assert.Equal(original, vm.Jobs);
            Assert.Empty(vm.Collector.State.Jobs);
            AssertDownloadSortIndicator(grid, "Date", ListSortDirection.Descending, "↓");
            foreach (string name in new[] { "State", "Details" })
            {
                DataGridColumnHeader header = DownloadSortingHeader(grid, name);
                Assert.False(header.Column.CanUserSort);
                Assert.Null(header.Column.SortDirection);
                TextBlock indicator = DownloadSortingIndicator(header);
                Assert.False(indicator.IsVisible);
            }
            CaptureLocalLayout(dialog, $"download-sort-{width}x{height}-date.png");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task DownloadSorting_UserChoiceSurvivesProgressFilteringNewRowsAndReopening() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadJobViewModel[] original = AddDownloadSortingRows(vm);
        DataRetentionDialog? dialog = new() { DataContext = vm, ShowActivated = false };
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            grid.SelectedItem = original[1];

            await InvokeDownloadSortHeader(grid, "Equity");
            Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, DownloadSortingSymbols(grid));
            AssertDownloadSortIndicator(grid, "Equity", ListSortDirection.Ascending, "A–Z");
            Assert.Null(DownloadSortingHeader(grid, "Date").Column.SortDirection);

            await InvokeDownloadSortHeader(grid, "Equity");
            Assert.Equal(new[] { "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));
            AssertDownloadSortIndicator(grid, "Equity", ListSortDirection.Descending, "Z–A");
            CaptureLocalLayout(dialog, "download-sort-equity-descending.png");

            var discovered = new CollectionJob { Symbol = "TSLA", SessionDate = new(2026, 9, 9), IsAvailabilityProbe = true };
            var added = new DownloadJobViewModel(discovered);
            vm.Jobs.Add(added);
            await SettleLocalLayout(dialog);
            Assert.Equal(new[] { "TSLA", "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));
            added.Update(discovered with { Status = CollectionJobStatus.Downloading });
            await SettleLocalLayout(dialog);
            Assert.Equal(new[] { "TSLA", "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));

            added.Update(discovered with { Status = CollectionJobStatus.Unavailable });
            await SettleLocalLayout(dialog);
            Assert.Equal(new[] { "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));
            Assert.Same(original[1], grid.SelectedItem);
            AssertDownloadSortIndicator(grid, "Equity", ListSortDirection.Descending, "Z–A");
            Assert.Equal(original.Append(added), vm.Jobs);
            Assert.Empty(vm.Collector.State.Jobs);

            dialog.Close();
            dialog = new DataRetentionDialog { DataContext = vm, ShowActivated = false };
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            Assert.Equal(new[] { "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));
            AssertDownloadSortIndicator(grid, "Equity", ListSortDirection.Descending, "Z–A");
            Assert.Null(DownloadSortingHeader(grid, "Date").Column.SortDirection);

            await InvokeDownloadSortHeader(grid, "Date");
            Assert.Equal(new[] { new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8) },
                grid.Items.Cast<DownloadJobViewModel>().Select(row => row.SessionDate));
            AssertDownloadSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑");
            Assert.Null(DownloadSortingHeader(grid, "Equity").Column.SortDirection);
            await InvokeDownloadSortHeader(grid, "Date");
            Assert.Equal(new[] { new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 3) },
                grid.Items.Cast<DownloadJobViewModel>().Select(row => row.SessionDate));
            AssertDownloadSortIndicator(grid, "Date", ListSortDirection.Descending, "↓");
        }
        finally { dialog?.Close(); }
    });

    private static DownloadJobViewModel[] AddDownloadSortingRows(DataRetentionViewModel vm)
    {
        DownloadJobViewModel[] rows =
        [
            new(new CollectionJob { Symbol = "MSFT", SessionDate = new(2026, 9, 3) }),
            new(new CollectionJob { Symbol = "NVDA", SessionDate = new(2026, 9, 8) }),
            new(new CollectionJob { Symbol = "AAPL", SessionDate = new(2026, 9, 8) }),
        ];
        foreach (DownloadJobViewModel row in rows) vm.Jobs.Add(row);
        return rows;
    }

    private static string[] DownloadSortingSymbols(DataGrid grid) =>
        grid.Items.Cast<DownloadJobViewModel>().Select(row => row.Symbol).ToArray();

    private static DataGridColumnHeader DownloadSortingHeader(DataGrid grid, string name) =>
        Assert.Single(FindRetentionVisuals<DataGridColumnHeader>(grid), header => Equals(header.Column?.Header, name));

    private static TextBlock DownloadSortingIndicator(DataGridColumnHeader header) =>
        Assert.IsType<TextBlock>(header.Template.FindName("DownloadSortIndicator", header));

    private static void AssertDownloadSortIndicator(DataGrid grid, string headerName, ListSortDirection direction, string text)
    {
        DataGridColumnHeader header = DownloadSortingHeader(grid, headerName);
        Assert.Equal(direction, header.Column.SortDirection);
        TextBlock indicator = DownloadSortingIndicator(header);
        Assert.True(indicator.IsVisible);
        Assert.Contains(text, indicator.Text);
        Assert.True(indicator.ActualWidth > 0);
        Point origin = indicator.TransformToAncestor(header).Transform(new Point());
        Assert.True(origin.X >= 0 && origin.X + indicator.ActualWidth <= header.ActualWidth + 1);
    }

    private static async Task InvokeDownloadSortHeader(DataGrid grid, string name)
    {
        MethodInfo click = typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        click.Invoke(DownloadSortingHeader(grid, name), null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        grid.UpdateLayout();
    }
}
