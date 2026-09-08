using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PriceSentinel3000.App.Dialogs;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
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
        vm.DownloadNowCommand.Execute(null);
        Task download = vm.DownloadNowCommand.ExecutionTask!;
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
