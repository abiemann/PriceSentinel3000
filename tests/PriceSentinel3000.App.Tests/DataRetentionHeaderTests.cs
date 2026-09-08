using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PriceSentinel3000.App.Converters;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task RetentionHeader_ShowsQueuedProgressAndPauseStateThenHidesOnCompletion() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(
            new CollectionJob { Symbol = "NFLX", Status = CollectionJobStatus.Complete },
            new CollectionJob { Symbol = "SOXL" });
        await using var workspace = new TestWorkspace();
        DataRetentionViewModel vm = fixture.ViewModel;
        workspace.ViewModel.DataRetention = vm;
        var header = new AppHeaderView { DataContext = workspace.ViewModel };
        var window = new Window { Content = header, Width = 1500, Height = 110, ShowActivated = false };
        ToolTip? tooltip = null;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = (Button)header.FindName("RetainHighResolutionDataButton");
            var progress = (ProgressBar)header.FindName("HeaderDownloadProgress");
            Assert.True(progress.IsVisible);
            Assert.Equal(50d, progress.Value);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(Color.FromRgb(0xE8, 0xAB, 0x4D), ((SolidColorBrush)progress.Foreground).Color);
            double buttonWidth = button.ActualWidth;
            Assert.Equal(34d, button.ActualHeight);
            Point progressOrigin = progress.TransformToAncestor(button).Transform(new Point());
            Assert.True(progressOrigin.Y >= 0 && progressOrigin.Y + progress.ActualHeight <= button.ActualHeight);
            Assert.Equal(4d, progress.ActualHeight);

            await vm.PauseDownloadsCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Paused", vm.DownloadState);
            Assert.True(progress.IsVisible);
            tooltip = (ToolTip)button.ToolTip;
            tooltip.PlacementTarget = button;
            tooltip.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains(FindRetentionVisuals<TextBlock>(tooltip), text => text.Text == vm.DownloadHeading);
            Assert.Contains(FindRetentionVisuals<TextBlock>(tooltip), text => text.Text == vm.DownloadDetail);

            await vm.PauseDownloadsCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Complete", vm.DownloadState);
            Assert.False(progress.IsVisible);
            Assert.Equal(100d, progress.Value);
            Assert.Equal(buttonWidth, button.ActualWidth);
            Assert.Contains(FindRetentionVisuals<TextBlock>(tooltip), text => text.Text == vm.DownloadHeading);
            Assert.Contains(FindRetentionVisuals<TextBlock>(tooltip), text => text.Text == vm.DownloadDetail);
            Assert.Contains(FindRetentionVisuals<TextBlock>(tooltip), text => text.Text == vm.JobSummary);
        }
        finally
        {
            if (tooltip is not null) tooltip.IsOpen = false;
            window.Close();
        }
    });

    [Fact]
    public Task RetentionDialog_CloseKeepsDownloadingAndReopenShowsTheLatestSharedQueue() => host.RunAsync(async () =>
    {
        await using var fixture = new BackgroundRetentionFixture();
        await using var workspace = new TestWorkspace();
        DataRetentionViewModel vm = fixture.ViewModel;
        workspace.ViewModel.DataRetention = vm;
        ResourceDictionary resources = System.Windows.Application.Current.Resources;
        bool addedConverter = !resources.Contains("TradingModeToAngleConverter");
        if (addedConverter) resources.Add("TradingModeToAngleConverter", new TradingModeToAngleConverter());
        MainWindow? window = null;
        try
        {
            window = new MainWindow(workspace.ViewModel) { ShowActivated = false };
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AppHeaderView header = Assert.Single(FindRetentionVisuals<AppHeaderView>(window));
            var button = (Button)header.FindName("RetainHighResolutionDataButton");
            var progress = (ProgressBar)header.FindName("HeaderDownloadProgress");
            vm.Start();
            await fixture.Provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(progress.IsVisible);
            Assert.True(progress.IsIndeterminate);
            Assert.Equal(0d, progress.Value);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DataRetentionDialog first = Assert.Single(window.OwnedWindows.OfType<DataRetentionDialog>());
            Assert.Same(vm, first.DataContext);
            Assert.True(((TabItem)first.FindName("ScheduleDownloadsTab")).IsSelected);
            first.Close();
            Assert.Empty(window.OwnedWindows.OfType<DataRetentionDialog>());
            fixture.Provider.ReleaseFirst.TrySetResult();
            await fixture.Provider.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(progress.IsVisible);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(50d, progress.Value);
            Assert.Equal(1, vm.DownloadProcessed);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DataRetentionDialog reopened = Assert.Single(window.OwnedWindows.OfType<DataRetentionDialog>());
            Assert.NotSame(first, reopened);
            Assert.Same(vm, reopened.DataContext);
            Assert.True(((TabItem)reopened.FindName("ScheduleDownloadsTab")).IsSelected);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(50d, ((ProgressBar)reopened.FindName("DownloadProgress")).Value);
            Assert.Equal(vm.DownloadHeading, ((TextBlock)reopened.FindName("DownloadActivityHeading")).Text);
            reopened.Close();
            fixture.Provider.ReleaseSecond.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (vm.HasDownloadWork) await Task.Delay(10, timeout.Token);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, vm.DownloadProcessed);
            Assert.False(progress.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DataRetentionDialog completed = Assert.Single(window.OwnedWindows.OfType<DataRetentionDialog>());
            Assert.Same(vm, completed.DataContext);
            Assert.True(((TabItem)completed.FindName("ScheduleDownloadsTab")).IsSelected);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(100d, ((ProgressBar)completed.FindName("DownloadProgress")).Value);
            completed.Close();
        }
        finally
        {
            fixture.Provider.ReleaseFirst.TrySetResult();
            fixture.Provider.ReleaseSecond.TrySetResult();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (window is not null)
            {
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
            }
            await workspace.ViewModel.ShutdownAsync();
            if (window is not null) await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (addedConverter) resources.Remove("TradingModeToAngleConverter");
        }
    });

    private sealed class BackgroundRetentionFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pricesentinel-header-tests", Guid.NewGuid().ToString("N"));
        public BackgroundRetentionProvider Provider { get; } = new();
        public DataRetentionViewModel ViewModel { get; }
        public BackgroundRetentionFixture()
        {
            string libraryRoot = Path.Combine(_root, "library");
            var store = new JsonCollectionStateStore(Path.Combine(_root, "state.json"));
            var clock = new TestClock { Now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero) };
            store.Save(new CollectionState
            {
                Settings = new CollectionSettings { LibraryRootPath = libraryRoot },
                Jobs = new[] { "NFLX", "SOXL" }.Select(symbol => new CollectionJob
                {
                    Symbol = symbol, LibraryRootPath = libraryRoot, SessionDate = new(2026, 9, 4), QueuedAtUtc = clock.Now,
                }).ToArray(),
            });
            var collector = new MarketDataCollector(store, Provider, root => new JsonMarketDataLibrary(root), clock,
                new CollectionRunOptions { MaximumRequestsPerTick = 1, MinimumRequestInterval = TimeSpan.Zero });
            ViewModel = new(collector, Provider, Provider.Identity, Provider.Identity,
                root => new JsonMarketDataLibrary(root), _ => Task.CompletedTask, () => true, clock: clock);
        }
        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class BackgroundRetentionProvider : IMarketHistoryProvider
    {
        private int _requests;
        public RetentionProvider Identity { get; } = new(() => true);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _requests);
            (index == 1 ? FirstStarted : SecondStarted).TrySetResult();
            await (index == 1 ? ReleaseFirst.Task : ReleaseSecond.Task).WaitAsync(cancellationToken);
            return await Identity.DownloadHistoryAsync(request, cancellationToken);
        }
    }
}
