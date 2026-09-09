using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task RetentionDialog_LibraryShowsFullDayCoverageAndPreservesSelection() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        var vm = fixture.ViewModel;
        var date = new DateOnly(2026, 9, 8);
        DateTimeOffset from = CollectionSchedule.GetSessionWindow(date).FromUtc;
        DateTimeOffset through = from.AddMinutes(195);
        var dataset = new HistoricalDatasetInfo(new string('a', 64), "NFLX.json", "Robinhood", "nflx", "NFLX",
            date, 15, "split", "unversioned", "regular", through,
            new(from, through, from, through, 780, 780, true, true, []));
        vm.Datasets.Add(dataset);
        LibraryDaySummary day = Assert.Single(LibraryDaySummary.Create([dataset]));
        vm.LibraryDays.Add(day);
        var dialog = new DataRetentionDialog { DataContext = vm, ShowActivated = false };
        try
        {
            dialog.Show();
            ((TabControl)dialog.FindName("RetentionTabs")).SelectedIndex = 2;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            Assert.DoesNotContain(FindRetentionVisuals<CheckBox>(dialog), checkbox =>
                Equals(checkbox.Content, "Replay from local files only"));
            var coverage = Assert.IsType<DataGridTextColumn>(Assert.Single(grid.Columns,
                column => Equals(column.Header, "Day coverage")));
            var cell = Assert.IsType<TextBlock>(coverage.GetCellContent(day));
            Assert.Equal("50%", cell.Text);
            Assert.Contains("780", Assert.IsType<string>(cell.ToolTip));
            Assert.Contains("1,560", Assert.IsType<string>(cell.ToolTip));
            Assert.DoesNotContain(grid.Columns, column => Equals(column.Header, "Revision hash") || Equals(column.Header, "Complete"));
            grid.SelectedItem = day;
            Assert.Same(day, vm.SelectedLibraryDay);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task RetentionDialog_ActivityAndUsableQueueRemainVisibleAtSupportedSizes(int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        var vm = fixture.ViewModel;
        fixture.Connected = true;
        await fixture.SaveSingleSymbol();
        fixture.Provider.HoldDownloads = true;
        Task download = StartQueuedRetentionDownloadsAsync(vm);
        Task first = await Task.WhenAny(fixture.Provider.DownloadStarted.Task, download).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ReferenceEquals(first, fixture.Provider.DownloadStarted.Task), vm.Status);
        vm.AutomaticDownloadsEnabled = !vm.SavedAutomaticDownloadsEnabled;
        DataRetentionDialog? dialog = null;
        try
        {
            dialog = new DataRetentionDialog { DataContext = vm, Width = width, Height = height, ShowActivated = false };
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            dialog.UpdateLayout();

            var card = (Border)dialog.FindName("DownloadActivityCard");
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            var pause = (Button)dialog.FindName("PauseDownloadsButton");
            var guidance = (TextBlock)dialog.FindName("DownloadInteractionGuidance");
            var progress = (ProgressBar)dialog.FindName("DownloadProgress");
            AssertInsideWindow(dialog, card);
            AssertInsideWindow(dialog, grid);
            AssertInsideWindow(dialog, pause);
            Assert.True(grid.ActualHeight >= 125, $"Queue height was only {grid.ActualHeight:0}px.");
            Assert.True(progress.ActualWidth >= 100);
            Assert.True(pause.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(guidance.Text));
            Assert.Equal(Visibility.Visible, ((TextBlock)dialog.FindName("UnsavedScheduleWarning")).Visibility);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting((TextBlock)dialog.FindName("DownloadActivityHeading")));
            Assert.DoesNotContain(FindRetentionVisuals<TextBox>(dialog), field =>
                AutomationProperties.GetName(field) is "Download from date" or "Download through date");
            Assert.DoesNotContain(FindRetentionVisuals<ComboBox>(dialog), field =>
                AutomationProperties.GetName(field) == "Download session coverage");
            Assert.Contains("overnight", ((TextBlock)dialog.FindName("DownloadCoverageGuidance")).Text);
            Assert.Equal(vm.ScheduleHelp, ((TextBlock)dialog.FindName("ScheduleHelpText")).Text);
            Assert.DoesNotContain(FindRetentionVisuals<Button>(dialog), button => Equals(button.Content, "NOW"));
            Button downloadAvailable = Assert.Single(FindRetentionVisuals<Button>(dialog),
                button => Equals(button.Content, "DOWNLOAD GAPS NOW"));
            Assert.Same(vm.DownloadNowCommand, downloadAvailable.Command);
            var forced = (Button)dialog.FindName("ForcedDownloadButton");
            Assert.Equal("FORCED DOWNLOAD", forced.Content);
            Assert.Same(vm.ForcedDownloadCommand, forced.Command);
            Assert.False(forced.IsEnabled);
            AssertInsideWindow(dialog, forced);
            var clear = (Button)dialog.FindName("ClearDownloadQueueButton");
            Assert.Same(vm.ClearDownloadQueueCommand, clear.Command);
            Assert.False(clear.IsEnabled);
            AssertInsideWindow(dialog, downloadAvailable);
            AssertInsideWindow(dialog, clear);
            Point downloadPosition = downloadAvailable.TranslatePoint(new Point(), dialog);
            Point clearPosition = clear.TranslatePoint(new Point(), dialog);
            Point forcedPosition = forced.TranslatePoint(new Point(), dialog);
            Assert.True(forcedPosition.X >= downloadPosition.X + downloadAvailable.ActualWidth);
            Assert.True(clearPosition.X >= forcedPosition.X + forced.ActualWidth);
            Assert.Equal(downloadPosition.Y, forcedPosition.Y);
            Assert.Equal(downloadPosition.Y, clearPosition.Y);
            Assert.DoesNotContain(FindRetentionVisuals<Button>(dialog), button => Equals(button.Content, "RETRY MISSING"));
            AssertInsideWindow(dialog, (TextBlock)dialog.FindName("DownloadAvailableGuidance"));

            var tabs = (TabControl)dialog.FindName("RetentionTabs");
            tabs.SelectedIndex = 0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(card.IsVisible);
            Assert.True(pause.IsVisible);
            Assert.True(tabs.IsEnabled);
            Assert.All(FindRetentionVisuals<TextBox>(dialog), field => Assert.True(field.IsEnabled));
        }
        finally
        {
            vm.CancelDownloadsCommand.Execute(null);
            await download;
            dialog?.Close();
        }
    });

    private static void AssertInsideWindow(Window window, FrameworkElement element)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        Assert.True(origin.X >= 0 && origin.Y >= 0);
        Assert.True(origin.X + element.ActualWidth <= window.ActualWidth + 1);
        Assert.True(origin.Y + element.ActualHeight <= window.ActualHeight + 1);
    }

    private static IEnumerable<T> FindRetentionVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T nested in FindRetentionVisuals<T>(child)) yield return nested;
        }
    }
}
