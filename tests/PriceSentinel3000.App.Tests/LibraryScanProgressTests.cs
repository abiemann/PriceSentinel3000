using System.IO;
using System.Windows;
using System.Windows.Controls;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790, false)]
    [InlineData(860, 620, true)]
    public Task LibraryScan_ProgressIsVisibleWithoutMovingToolbarAndResetsAfterCompletion(
        int width, int height, bool fail) => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        using var library = new HeldProgressLibrary(fixture.LibraryRoot) { Fail = fail };
        await using DataRetentionViewModel vm = CreateScanViewModel(fixture, library);
        HistoricalDatasetInfo original = ScanDataset("NFLX");
        vm.Datasets.Add(original);
        foreach (var day in LibraryDaySummary.Create([original], fixture.Clock.Now)) vm.LibraryDays.Add(day);
        int uiThread = Environment.CurrentManagedThreadId;
        var progressThreads = new List<int>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DataRetentionViewModel.LibraryScanPercent))
                progressThreads.Add(Environment.CurrentManagedThreadId);
        };
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        Task? scan = null;
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var button = (Button)dialog.FindName("RescanLibraryButton");
            var progressText = (TextBlock)dialog.FindName("LibraryScanProgressText");
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            Size buttonSize = button.RenderSize;
            Point tablePosition = grid.TranslatePoint(new Point(), dialog);
            Assert.True(button.IsEnabled);
            Assert.Equal(Visibility.Collapsed, progressText.Visibility);

            scan = vm.ScanLibraryCommand.ExecuteAsync();
            await library.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettleLocalLayout(dialog);
            Assert.True(vm.IsLibraryScanning);
            Assert.True(vm.IsBusy);
            Assert.False(button.IsEnabled);
            Assert.Equal("Processing\n0% Complete", progressText.Text);
            Assert.Equal(Visibility.Visible, progressText.Visibility);

            library.Reporter!.Report(42);
            await SettleLocalLayout(dialog);
            Assert.Equal("Processing\n42% Complete", progressText.Text);
            Assert.Equal(buttonSize, button.RenderSize);
            Assert.Equal(tablePosition, grid.TranslatePoint(new Point(), dialog));
            Assert.True(progressText.ActualHeight <= button.ActualHeight);
            Assert.Equal(1, progressText.Opacity);
            CaptureLocalLayout(dialog, $"library-scan-processing-{width}x{height}.png");
            await vm.ScanLibraryAsync();
            Assert.Equal(1, library.Calls);

            library.Reporter.Report(100);
            await SettleLocalLayout(dialog);
            Assert.Equal(99, vm.LibraryScanPercent);
            Assert.Same(original, Assert.Single(vm.Datasets));
            library.Release.Set();
            await scan;
            await SettleLocalLayout(dialog);
            Assert.False(vm.IsLibraryScanning);
            Assert.False(vm.IsBusy);
            Assert.True(button.IsEnabled);
            Assert.Equal("RESCAN LIBRARY", vm.LibraryScanButtonText);
            Assert.Equal(Visibility.Collapsed, progressText.Visibility);
            Assert.Equal(buttonSize, button.RenderSize);
            Assert.Equal(tablePosition, grid.TranslatePoint(new Point(), dialog));
            if (fail)
            {
                Assert.Equal("Cannot read library.", vm.Status);
                Assert.Same(original, Assert.Single(vm.Datasets));
                Assert.Equal(99, vm.LibraryScanPercent);
            }
            else
            {
                Assert.Equal(100, vm.LibraryScanPercent);
                Assert.Empty(vm.Datasets);
                Assert.Contains("Found 0 daily entries", vm.Status);
            }

            library.Reporter.Report(17);
            await SettleLocalLayout(dialog);
            Assert.Equal(fail ? 99 : 100, vm.LibraryScanPercent);
            library.Fail = false;
            await vm.ScanLibraryCommand.ExecuteAsync();
            Assert.Equal(2, library.Calls);
            Assert.Equal(100, vm.LibraryScanPercent);
            Assert.Empty(vm.Datasets);
            Assert.All(progressThreads, thread => Assert.Equal(uiThread, thread));
            Assert.Equal(0, fixture.Provider.Calls);
        }
        finally
        {
            library.Release.Set();
            if (scan is not null) await scan;
            dialog.Close();
        }
    });

    private sealed class HeldProgressLibrary(string root) : IMarketDataLibrary, IDisposable
    {
        public string RootPath => root;
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public IProgress<int>? Reporter { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public MarketDataLibraryScan ConsolidateDailyFiles(IProgress<int> progress)
        {
            Calls++;
            Reporter = progress;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Scan test was not released.");
            if (Fail) throw new IOException("Cannot read library.");
            return new([], []);
        }
        public MarketDataLibraryScan Scan() => throw new NotSupportedException();
        public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download) => throw new NotSupportedException();
        public HistoricalDataset Read(string datasetHash) => throw new NotSupportedException();
        public HistoricalDataQueryResult Query(HistoricalDataQuery query) => throw new NotSupportedException();
        public void Dispose() => Release.Dispose();
    }
}
