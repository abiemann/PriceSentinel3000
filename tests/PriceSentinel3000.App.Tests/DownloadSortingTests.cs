using System.ComponentModel;
using System.Reflection;
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
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Descending),
                (nameof(DownloadJobViewModel.Symbol), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Descending, "↓ 1");
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 2");
            AssertLocalSortIndicator(grid, "State", null, "↕");
            Assert.Equal(nameof(DownloadJobViewModel.StateCoveragePercent), DownloadSortingHeader(grid, "State").Column.SortMemberPath);
            DataGridColumnHeader details = DownloadSortingHeader(grid, "Details");
            Assert.False(details.Column.CanUserSort);
            Assert.Null(details.Column.SortDirection);
            Assert.Null(details.ToolTip);
            Assert.False(Assert.IsType<TextBlock>(details.Template.FindName("LibrarySortIndicator", details)).IsVisible);
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

            await SetLocalPrimaryAscending(grid, "Equity");
            Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, DownloadSortingSymbols(grid));
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", null, "↕");

            await InvokeDownloadSortHeader(grid, "Equity");
            Assert.Equal(new[] { "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Descending, "↓ 1");
            await InvokeLocalSecondarySort(grid, "State");
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.Symbol), ListSortDirection.Descending),
                (nameof(DownloadJobViewModel.StateCoveragePercent), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Ascending, "↑ 2");
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
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Descending, "↓ 1");
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Ascending, "↑ 2");
            Assert.Equal(original.Append(added), vm.Jobs);
            Assert.Empty(vm.Collector.State.Jobs);

            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Descending, "↓ 1");
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Ascending, "↑ 2");

            dialog.Close();
            dialog = new DataRetentionDialog { DataContext = vm, ShowActivated = false };
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.Symbol), ListSortDirection.Descending),
                (nameof(DownloadJobViewModel.StateCoveragePercent), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Ascending, "↑ 2");
            Assert.Equal(new[] { "NVDA", "MSFT", "AAPL" }, DownloadSortingSymbols(grid));
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Descending, "↓ 1");
            AssertLocalSortIndicator(grid, "Date", null, "↕");

            await InvokeDownloadSortHeader(grid, "Date");
            Assert.Equal(new[] { new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8) },
                grid.Items.Cast<DownloadJobViewModel>().Select(row => row.SessionDate));
            AssertLocalSortDescriptions(grid, (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Ascending));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Equity", null, "↕");
            AssertLocalSortIndicator(grid, "State", null, "↕");
            await InvokeDownloadSortHeader(grid, "Date");
            Assert.Equal(new[] { new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 3) },
                grid.Items.Cast<DownloadJobViewModel>().Select(row => row.SessionDate));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Descending, "↓ 1");
        }
        finally { dialog?.Close(); }
    });

    [Fact]
    public Task DownloadSorting_SecondaryDateReversesWithoutChangingPrimaryEquity() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadJobViewModel[] original =
        [
            new(new CollectionJob { Symbol = "MSFT", SessionDate = new(2026, 9, 3) }),
            new(new CollectionJob { Symbol = "AAPL", SessionDate = new(2026, 9, 3) }),
            new(new CollectionJob { Symbol = "MSFT", SessionDate = new(2026, 9, 8) }),
            new(new CollectionJob { Symbol = "AAPL", SessionDate = new(2026, 9, 8) }),
        ];
        foreach (DownloadJobViewModel row in original) vm.Jobs.Add(row);
        var dialog = new DataRetentionDialog { DataContext = vm, ShowActivated = false };
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");

            await SetLocalPrimaryAscending(grid, "Equity");
            await InvokeLocalSecondarySort(grid, "Date");
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.Symbol), ListSortDirection.Ascending),
                (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Ascending));
            Assert.Equal(new[] { original[1], original[3], original[0], original[2] }, grid.Items.Cast<DownloadJobViewModel>());
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 2");

            await InvokeLocalSecondarySort(grid, "Date");
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.Symbol), ListSortDirection.Ascending),
                (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Descending));
            Assert.Equal(new[] { original[3], original[1], original[2], original[0] }, grid.Items.Cast<DownloadJobViewModel>());
            AssertLocalSortIndicator(grid, "Equity", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Descending, "↓ 2");
            Assert.Equal(original, vm.Jobs);
            CaptureLocalLayout(dialog, "download-sort-equity-then-date.png");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task DownloadSorting_StateUsesDisplayedNumericCoverageAndUpdatesWithinPrimaryDate() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        CollectionJob[] jobs =
        [
            new() { Symbol = "HIGH", SessionDate = new(2026, 9, 3), SavedCoveragePercent = 100m },
            new() { Symbol = "LOW", SessionDate = new(2026, 9, 3), SavedCoveragePercent = 9.25m },
            new() { Symbol = "MID", SessionDate = new(2026, 9, 3), SavedCoveragePercent = 80m },
            new() { Symbol = "FAILED", SessionDate = new(2026, 9, 8), Status = CollectionJobStatus.Failed, SavedCoveragePercent = 95m },
            new() { Symbol = "UNAVAILABLE", SessionDate = new(2026, 9, 8), Status = CollectionJobStatus.Unavailable, SavedCoveragePercent = 65m },
            new() { Symbol = "UNKNOWN", SessionDate = new(2026, 9, 8) },
            new() { Symbol = "OVER", SessionDate = new(2026, 9, 8), SavedCoveragePercent = 120m },
            new() { Symbol = "UNDER", SessionDate = new(2026, 9, 8), SavedCoveragePercent = -5m },
        ];
        DownloadJobViewModel[] original = jobs.Select(job => new DownloadJobViewModel(job)).ToArray();
        foreach (DownloadJobViewModel row in original) vm.Jobs.Add(row);
        Assert.Equal(new decimal?[] { 100m, 9.25m, 80m, 0m, 0m, null, 100m, 0m }, original.Select(row => row.StateCoveragePercent));
        var dialog = new DataRetentionDialog { DataContext = vm, Width = 860, Height = 620, ShowActivated = false };
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");

            await InvokeDownloadSortHeader(grid, "State");
            AssertLocalSortDescriptions(grid, (nameof(DownloadJobViewModel.StateCoveragePercent), ListSortDirection.Ascending));
            Assert.Equal(new decimal?[] { null, 0m, 0m, 0m, 9.25m, 80m, 100m, 100m },
                grid.Items.Cast<DownloadJobViewModel>().Select(row => row.StateCoveragePercent));
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "Equity", null, "↕");
            AssertLocalSortIndicator(grid, "Date", null, "↕");
            await InvokeDownloadSortHeader(grid, "State");
            Assert.Equal(new decimal?[] { 100m, 100m, 80m, 9.25m, 0m, 0m, 0m, null },
                grid.Items.Cast<DownloadJobViewModel>().Select(row => row.StateCoveragePercent));
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Descending, "↓ 1");

            await SetLocalPrimaryAscending(grid, "Date");
            await InvokeLocalSecondarySort(grid, "State");
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Ascending),
                (nameof(DownloadJobViewModel.StateCoveragePercent), ListSortDirection.Ascending));
            Assert.Equal(new[] { original[1], original[2], original[0] }, grid.Items.Cast<DownloadJobViewModel>().Take(3));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Ascending, "↑ 2");
            await InvokeLocalSecondarySort(grid, "State");
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Ascending),
                (nameof(DownloadJobViewModel.StateCoveragePercent), ListSortDirection.Descending));
            Assert.Equal(new[] { original[0], original[2], original[1] }, grid.Items.Cast<DownloadJobViewModel>().Take(3));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Descending, "↓ 2");

            grid.SelectedItem = original[1];
            original[1].Update(jobs[1] with { SavedCoveragePercent = 90m });
            await SettleLocalLayout(dialog);
            Assert.Equal(new[] { original[0], original[1], original[2] }, grid.Items.Cast<DownloadJobViewModel>().Take(3));
            Assert.Same(original[1], grid.SelectedItem);
            original[0].Update(jobs[0] with { Status = CollectionJobStatus.Failed });
            await SettleLocalLayout(dialog);
            Assert.Equal(new[] { original[1], original[2], original[0] }, grid.Items.Cast<DownloadJobViewModel>().Take(3));
            AssertLocalSortDescriptions(grid,
                (nameof(DownloadJobViewModel.SessionDate), ListSortDirection.Ascending),
                (nameof(DownloadJobViewModel.StateCoveragePercent), ListSortDirection.Descending));
            AssertLocalSortIndicator(grid, "Date", ListSortDirection.Ascending, "↑ 1");
            AssertLocalSortIndicator(grid, "State", ListSortDirection.Descending, "↓ 2");
            Assert.Equal(original, vm.Jobs);
            Assert.Empty(vm.Collector.State.Jobs);
            CaptureLocalLayout(dialog, "download-sort-date-then-state.png");
        }
        finally { dialog.Close(); }
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

    private static async Task InvokeDownloadSortHeader(DataGrid grid, string name)
    {
        MethodInfo click = typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        click.Invoke(DownloadSortingHeader(grid, name), null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        grid.UpdateLayout();
    }
}
