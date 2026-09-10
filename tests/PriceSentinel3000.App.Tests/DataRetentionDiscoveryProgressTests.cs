using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task DownloadProgress_OlderDiscoveryRemainsVisiblyBusyAfterAllRowsFinish_WithoutMovingTheLayout() => host.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "pricesentinel-discovery-progress-" + Guid.NewGuid().ToString("N"));
        var clock = new TestClock { Now = new(2026, 9, 9, 4, 0, 15, TimeSpan.Zero) };
        DateOnly today = new(2026, 9, 9), older = today.AddDays(-1);
        var library = new JsonMarketDataLibrary(Path.Combine(root, "library"));
        DateTimeOffset start = clock.Now.AddSeconds(-15);
        library.Save(new("Robinhood", "id-AAPL", "AAPL", 15, "split", "robinhood-split-unversioned", "24_5",
            clock.Now, start, clock.Now, [new(start, clock.Now, clock.Now, 80m, 81m, 79m, 80.5m, 100m)]));
        var index = new JsonCollectionGapIndex(library.RootPath);
        var key = new CollectionGapKey("AAPL", "id-AAPL", older, "24_5", "split", "robinhood-split-unversioned");
        CollectionSessionWindow olderDay = CollectionSchedule.GetSessionWindow(older, "24_5");
        index.RecordDownloadAttempt(key, olderDay.FromUtc, olderDay.ThroughUtc, clock.Now);
        index.RecordDownloadAttempt(key, olderDay.FromUtc, olderDay.ThroughUtc, clock.Now);
        index.RecordAttempt(key, olderDay.FromUtc, olderDay.ThroughUtc, [new(olderDay.FromUtc, olderDay.ThroughUtc)],
            receivedCandles: false, clock.Now, retryAfterUtc: null);
        var run = new CollectionAvailabilityRun
        {
            AsOfDate = today, CurrentDate = today, LibraryRootPath = library.RootPath,
            Members = [new("AAPL", ProviderInstrumentId: "id-AAPL")],
        };
        var finished = new CollectionJob
        {
            Symbol = "AAPL", ProviderInstrumentId = "id-AAPL", SessionDate = today, SessionBounds = "24_5",
            LibraryRootPath = library.RootPath, QueuedAtUtc = clock.Now, RequestedThroughUtc = clock.Now,
            IsAvailabilityProbe = true, AvailabilityRunId = run.Id, Status = CollectionJobStatus.Complete,
        };
        var store = new JsonCollectionStateStore(Path.Combine(root, "state.json"));
        store.Save(new()
        {
            Settings = new() { LibraryRootPath = library.RootPath }, Jobs = [finished],
            AvailabilityRun = run with { CurrentJobIds = [finished.Id] },
        });
        using var held = new DiscoveryProgressLibrary(library, olderDay.FromUtc);
        var provider = new RetentionProvider(() => true);
        var collector = new MarketDataCollector(store, provider, _ => held, clock,
            new() { MinimumRequestInterval = TimeSpan.Zero }, folder => new JsonCollectionGapIndex(folder));
        var vm = new DataRetentionViewModel(collector, provider, provider, provider, _ => library,
            _ => Task.CompletedTask, () => true, clock: clock);
        await using var workspace = new TestWorkspace();
        workspace.ViewModel.DataRetention = vm;
        var header = new AppHeaderView { DataContext = workspace.ViewModel };
        var headerWindow = new Window
        {
            Content = header, Width = 1500, Height = 110, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        var dialog = CreateLocalLayoutDialog(vm, 1080, 790);
        Task? downloading = null;
        try
        {
            headerWindow.Show();
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            Assert.Equal("Waiting", vm.DownloadState);
            Assert.True(vm.HasDownloadWork);
            Assert.Equal(100d, vm.DownloadProgressPercent);
            Assert.False(vm.IsDownloadProgressIndeterminate);
            Assert.Equal(Visibility.Hidden, ((Viewbox)dialog.FindName("DownloadBusyIndicator")).Visibility);
            downloading = vm.CheckDownloadsAsync();
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SettleLocalLayout(dialog);
            var spinner = (Viewbox)dialog.FindName("DownloadBusyIndicator");
            var progress = (ProgressBar)dialog.FindName("DownloadProgress");
            var headerProgress = (ProgressBar)header.FindName("HeaderDownloadProgress");
            var info = (FrameworkElement)dialog.FindName("DownloadInfoButton");
            var heading = (FrameworkElement)dialog.FindName("DownloadActivityHeading");
            var card = (FrameworkElement)dialog.FindName("DownloadActivityCard");
            var tabs = (FrameworkElement)dialog.FindName("RetentionTabs");
            FrameworkElement[] stableElements = [info, heading, card, tabs];
            Point[] workingPositions = stableElements.Select(item => item.TranslatePoint(new Point(), dialog)).ToArray();
            double cardHeight = card.ActualHeight, tabsHeight = tabs.ActualHeight;

            Assert.True(collector.IsBusy);
            Assert.Equal(CollectionJobStatus.Complete, Assert.Single(vm.Jobs).Status);
            Assert.Equal("Working", vm.DownloadState);
            Assert.Equal(100d, vm.DownloadProgressPercent);
            Assert.Equal("Checking older history · AAPL · 2026-09-08", vm.DownloadHeading);
            Assert.Contains("before adding older dates", vm.DownloadDetail);
            Assert.True(vm.IsDownloadProgressIndeterminate);
            Assert.Equal(Visibility.Visible, spinner.Visibility);
            Assert.True(progress.IsIndeterminate);
            Assert.True(headerProgress.IsIndeterminate);
            Assert.True(headerProgress.IsVisible);
            Assert.Equal(100d, progress.Value);
            Assert.Equal(100d, headerProgress.Value);
            CaptureLocalLayout(dialog, "older-history-busy.png");

            held.Release.Set();
            await downloading.WaitAsync(TimeSpan.FromSeconds(10));
            await SettleLocalLayout(dialog);

            Assert.False(collector.IsBusy);
            Assert.Null(collector.Activity);
            Assert.Null(collector.State.AvailabilityRun);
            Assert.Equal("Complete", vm.DownloadState);
            Assert.Equal(100d, vm.DownloadProgressPercent);
            Assert.False(vm.IsDownloadProgressIndeterminate);
            Assert.Equal(Visibility.Hidden, spinner.Visibility);
            Assert.False(progress.IsIndeterminate);
            Assert.False(headerProgress.IsIndeterminate);
            Assert.Empty(provider.Requests);
            for (int i = 0; i < stableElements.Length; i++)
            {
                Point position = stableElements[i].TranslatePoint(new Point(), dialog);
                Assert.Equal(workingPositions[i].X, position.X, precision: 5);
                Assert.Equal(workingPositions[i].Y, position.Y, precision: 5);
            }
            Assert.Equal(cardHeight, card.ActualHeight, precision: 5);
            Assert.Equal(tabsHeight, tabs.ActualHeight, precision: 5);
        }
        finally
        {
            held.Release.Set();
            if (downloading is not null) await downloading.WaitAsync(TimeSpan.FromSeconds(10));
            dialog.Close();
            headerWindow.Close();
            await vm.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    });

    private sealed class DiscoveryProgressLibrary(IMarketDataLibrary inner, DateTimeOffset heldFrom) : IMarketDataLibrary, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public string RootPath => inner.RootPath;
        public MarketDataLibraryScan Scan() => inner.Scan();
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => inner.Save(download);
        public HistoricalDataset Read(string datasetHash) => inner.Read(datasetHash);
        public HistoricalDataQueryResult Query(HistoricalDataQuery query)
        {
            if (query.FromUtc == heldFrom)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The held discovery query was not released.");
            }
            return inner.Query(query);
        }
        public void Dispose() => Release.Dispose();
    }
}
