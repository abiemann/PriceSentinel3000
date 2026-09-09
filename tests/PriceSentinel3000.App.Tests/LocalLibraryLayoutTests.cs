using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790, 400)]
    [InlineData(860, 620, 230)]
    public Task LocalLibraryLayout_LargeCatalogKeepsUsableTableAndCompactQueueControls(
        int width, int height, int minimumGridHeight) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob { Symbol = "USO" });
        DataRetentionViewModel vm = fixture.ViewModel;
        AddLocalLayoutDatasets(vm);
        SetLocalLayoutText(vm, nameof(DataRetentionViewModel.Status), LongLocalLayoutDiagnostics());
        SetLocalLayoutText(vm, nameof(DataRetentionViewModel.LibraryDiagnostics), LongLocalLayoutDiagnostics());
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            var localTab = (TabItem)dialog.FindName("LocalLibraryTab");
            localTab.IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            var compact = (Border)dialog.FindName("LibraryDownloadSummaryCard");
            var full = (Border)dialog.FindName("DownloadActivityCard");
            var pause = (Button)dialog.FindName("LibraryPauseDownloadsButton");
            var diagnostics = (Expander)dialog.FindName("LibraryDiagnosticsExpander");

            Assert.Equal(120, grid.Items.Count);
            Assert.True(grid.ActualHeight >= minimumGridHeight,
                $"Local library grid at {width}x{height} was only {grid.ActualHeight:0}px high.");
            AssertInsideWindow(dialog, grid);
            Assert.True(compact.IsVisible);
            Assert.False(full.IsVisible);
            Assert.False(diagnostics.IsExpanded);
            AssertInsideWindow(dialog, compact);
            AssertInsideWindow(dialog, pause);
            Assert.True(pause.IsVisible);
            Assert.True(pause.IsEnabled);
            Assert.Same(vm.PauseDownloadsCommand, pause.Command);
            AssertLocalRevisionControlsAbsent(dialog, vm);
            AssertLocalFooterBounded(dialog);
            CaptureLocalLayout(dialog, $"local-library-{width}x{height}-default.png");

            HistoricalDatasetInfo last = vm.Datasets[^1];
            grid.ScrollIntoView(last);
            await SettleLocalLayout(dialog);
            DataGridRow row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(last));
            Assert.True(row.IsVisible);
            AssertInsideWindow(dialog, row);
            grid.SelectedItem = last;
            await SettleLocalLayout(dialog);
            Assert.Same(last, vm.SelectedDataset);
            ScrollViewer tableScroll = Assert.Single(FindRetentionVisuals<ScrollViewer>(grid));
            Assert.True(tableScroll.ScrollableHeight > 0);
            Assert.True(tableScroll.VerticalOffset > 0);

            await vm.PauseDownloadsCommand.ExecuteAsync();
            await SettleLocalLayout(dialog);
            Assert.Contains("RESUME", Assert.IsType<string>(pause.Content), StringComparison.OrdinalIgnoreCase);
            Assert.True(pause.IsEnabled);
            AssertInsideWindow(dialog, pause);

            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            Assert.True(full.IsVisible);
            Assert.False(compact.IsVisible);
            localTab.IsSelected = true;
            await SettleLocalLayout(dialog);
            Assert.True(grid.ActualHeight >= minimumGridHeight);
            Assert.Same(last, grid.SelectedItem);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task LocalLibraryLayout_LongDiagnosticsScrollWithoutTakingOverTheTableAndCollapseRestoresSpace(
        int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob { Symbol = "USO" });
        DataRetentionViewModel vm = fixture.ViewModel;
        AddLocalLayoutDatasets(vm);
        string details = LongLocalLayoutDiagnostics();
        SetLocalLayoutText(vm, nameof(DataRetentionViewModel.Status), details);
        SetLocalLayoutText(vm, nameof(DataRetentionViewModel.LibraryDiagnostics), details);
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            var expander = (Expander)dialog.FindName("LibraryDiagnosticsExpander");
            double collapsedHeight = grid.ActualHeight;
            HistoricalDatasetInfo selected = vm.Datasets[70];
            grid.SelectedItem = selected;
            grid.ScrollIntoView(selected);
            expander.IsExpanded = true;
            await SettleLocalLayout(dialog);

            FrameworkElement diagnosticText = Assert.IsAssignableFrom<FrameworkElement>(dialog.FindName("LibraryDiagnosticsText"));
            Assert.True(diagnosticText.IsVisible);
            AssertInsideWindow(dialog, expander);
            Assert.True(expander.ActualHeight <= 200, $"Diagnostics used {expander.ActualHeight:0}px.");
            Assert.True(grid.ActualHeight >= 150, $"Expanded diagnostics left only {grid.ActualHeight:0}px for datasets.");
            Assert.True(grid.ActualHeight < collapsedHeight);
            Assert.Contains(FindRetentionVisuals<ScrollViewer>(expander), scroll => scroll.ScrollableHeight > 0);
            Assert.Same(selected, vm.SelectedDataset);
            Assert.Same(selected, grid.SelectedItem);
            AssertLocalRevisionControlsAbsent(dialog, vm);
            AssertLocalFooterBounded(dialog);
            CaptureLocalLayout(dialog, $"local-library-{width}x{height}-diagnostics.png");

            expander.IsExpanded = false;
            await SettleLocalLayout(dialog);
            Assert.InRange(Math.Abs(grid.ActualHeight - collapsedHeight), 0, 1);
            Assert.Same(selected, grid.SelectedItem);
        }
        finally { dialog.Close(); }
    });

    private static DataRetentionDialog CreateLocalLayoutDialog(DataRetentionViewModel vm, int width, int height) => new()
    {
        DataContext = vm, Width = width, Height = height, ShowActivated = false,
        ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
    };

    private static async Task SettleLocalLayout(Window dialog)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        dialog.UpdateLayout();
    }

    private static void AssertLocalRevisionControlsAbsent(DataRetentionDialog dialog, DataRetentionViewModel vm)
    {
        Assert.DoesNotContain(FindRetentionVisuals<Button>(dialog), button =>
            ReferenceEquals(button.Command, vm.PinDatasetCommand) || ReferenceEquals(button.Command, vm.ClearPinsCommand));
        Assert.DoesNotContain(FindRetentionVisuals<TextBox>(dialog), text =>
            BindingOperations.GetBinding(text, TextBox.TextProperty)?.Path.Path == nameof(DataRetentionViewModel.ReplayPinnedHashes));
        Assert.DoesNotContain(FindRetentionVisuals<CheckBox>(dialog), checkbox =>
            BindingOperations.GetBinding(checkbox, ToggleButton.IsCheckedProperty)?.Path.Path == nameof(DataRetentionViewModel.ReplayUseLatestRevision));
        Assert.Null(dialog.FindName("LibraryReplayOptionsExpander"));
    }

    private static void AssertLocalFooterBounded(DataRetentionDialog dialog)
    {
        if (dialog.FindName("StatusSummaryText") is FrameworkElement footer && footer.IsVisible)
            Assert.True(footer.ActualHeight <= 28, $"The footer grew to {footer.ActualHeight:0}px.");
    }

    private static string LongLocalLayoutDiagnostics() => string.Join(Environment.NewLine,
        Enumerable.Range(1, 120).Select(index =>
            $"EQ{index:000} / 2026-09-08: saved 15-second candles retain source prices and timestamps; some requested bars are missing."));

    private static void SetLocalLayoutText(DataRetentionViewModel vm, string propertyName, string value) =>
        typeof(DataRetentionViewModel).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!
            .GetSetMethod(nonPublic: true)!.Invoke(vm, [value]);

    private static void AddLocalLayoutDatasets(DataRetentionViewModel vm)
    {
        var day = new DateOnly(2026, 9, 8);
        DateTimeOffset from = CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc;
        for (int index = 0; index < 120; index++)
        {
            DateTimeOffset through = from.AddHours(12 + index % 13);
            int candles = (int)((through - from).TotalSeconds / 15);
            vm.Datasets.Add(new(index.ToString("x64"), $"EQ{index:000}.json", "Robinhood", $"equity-{index}",
                $"EQ{index:000}", day, 15, "split", "robinhood-split-unversioned", "24_5", through.AddMinutes(15),
                new(from, through, from, through, candles, candles, true, true, [])));
        }
    }

    private static void CaptureLocalLayout(Window dialog, string name)
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("PRICESENTINEL_LAYOUT_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(captureDirectory)) return;
        Directory.CreateDirectory(captureDirectory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(captureDirectory, name));
        encoder.Save(output);
    }
}
