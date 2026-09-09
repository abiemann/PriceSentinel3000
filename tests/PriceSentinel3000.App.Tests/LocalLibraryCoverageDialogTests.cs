using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task LocalLibraryCoverage_DialogFitsAndDismissesWithoutClosingLibrary(int width, int height) =>
        host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryDaySummary day = AddCoverageDialogDatasets(vm);
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            grid.SelectedItem = day;
            await dialog.ShowLibraryCoverageAsync(day);
            await SettleLocalLayout(dialog);
            var overlay = (Grid)dialog.FindName("LibraryCoverageOverlay");
            var card = (Border)dialog.FindName("LibraryCoverageCard");
            var close = (Button)dialog.FindName("LibraryCoverageCloseButton");
            var content = (StackPanel)dialog.FindName("LibraryCoverageContent");
            var timeline = (LibraryCoverageTimelineControl)dialog.FindName("LibraryCoverageTimeline");

            Assert.True(overlay.IsVisible);
            AssertInsideWindow(dialog, card);
            AssertInsideWindow(dialog, close);
            AssertInsideWindow(dialog, timeline);
            Assert.True(timeline.ActualWidth >= 600);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(card));
            LibraryCoverageTimeline model = Assert.IsType<LibraryCoverageTimeline>(content.DataContext);
            Assert.Equal("AAPL", model.Symbol);
            Assert.Equal(day.TradingDate, model.Date);
            Assert.Contains(model.Blocks, block => block.State == LibraryCoverageBlockState.Partial);
            Assert.Contains(model.Blocks, block => block.State == LibraryCoverageBlockState.Complete);
            Assert.Contains(model.Blocks, block => block.State == LibraryCoverageBlockState.Missing);
            CaptureLocalLayout(dialog, $"coverage-{width}x{height}-full-day.png");

            card.RaiseEvent(CoverageMouseEvent(Mouse.MouseUpEvent));
            Assert.True(overlay.IsVisible);
            overlay.RaiseEvent(CoverageMouseEvent(Mouse.MouseDownEvent));
            Assert.True(overlay.IsVisible); // Keep the row behind covered until this click is released.
            overlay.RaiseEvent(CoverageMouseEvent(Mouse.MouseUpEvent));
            Assert.False(overlay.IsVisible);
            Assert.True(dialog.IsVisible);
            Assert.Same(day, grid.SelectedItem);

            await dialog.ShowLibraryCoverageAsync(day);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(overlay.IsVisible);
            Assert.True(dialog.IsVisible);

            await dialog.ShowLibraryCoverageAsync(day);
            close.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!,
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.False(overlay.IsVisible);
            Assert.True(dialog.IsVisible);

            await dialog.ShowLibraryCoverageAsync(day);
            content.DataContext = LibraryCoverageTimeline.Create(day.Symbol, day.TradingDate,
                TimeZoneInfo.Local, false, vm.Datasets, fixture.Clock.Now);
            await SettleLocalLayout(dialog);
            AssertInsideWindow(dialog, card);
            CaptureLocalLayout(dialog, $"coverage-{width}x{height}-daytime.png");
            Assert.Equal(0, fixture.Provider.DownloadCalls);
            Assert.Equal(0, fixture.ConnectionCalls);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task LocalLibraryCoverage_RowClickAndEnterOpenButHeaderClickDoesNot() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        LibraryDaySummary day = AddCoverageDialogDatasets(fixture.ViewModel);
        var dialog = CreateLocalLayoutDialog(fixture.ViewModel, 1080, 790);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            var overlay = (Grid)dialog.FindName("LibraryCoverageOverlay");
            var close = (Button)dialog.FindName("LibraryCoverageCloseButton");
            var header = FindRetentionVisuals<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).First();
            var headerClick = CoverageMouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            headerClick.Source = header;
            grid.RaiseEvent(headerClick);
            Assert.False(overlay.IsVisible);

            DataGridRow row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(day));
            var rowClick = CoverageMouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            rowClick.Source = row;
            grid.RaiseEvent(rowClick);
            await WaitForCoverageContent(dialog);
            Assert.True(overlay.IsVisible);
            Assert.True(rowClick.Handled);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            grid.SelectedItem = day;
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!,
                Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await WaitForCoverageContent(dialog);
            Assert.True(overlay.IsVisible);
            Assert.Equal(0, fixture.Provider.DownloadCalls);
        }
        finally { dialog.Close(); }
    });

    private static MouseButtonEventArgs CoverageMouseEvent(RoutedEvent routedEvent) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };

    private static async Task WaitForCoverageContent(DataRetentionDialog dialog)
    {
        var content = (StackPanel)dialog.FindName("LibraryCoverageContent");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (content.DataContext is not LibraryCoverageTimeline) await Task.Delay(10, timeout.Token);
        await SettleLocalLayout(dialog);
    }

    private static LibraryDaySummary AddCoverageDialogDatasets(DataRetentionViewModel vm)
    {
        var date = new DateOnly(2026, 9, 3);
        DateTimeOffset from = CollectionSchedule.GetSessionWindow(date, "24_5").FromUtc;
        for (int index = 0; index < 2; index++)
        {
            DateTimeOffset start = from.AddDays(index), end = start.AddDays(1);
            HistoricalGap[] gaps =
            [
                new(start.AddHours(6), start.AddHours(7.125)),
                new(start.AddHours(11), start.AddHours(13)),
                new(start.AddHours(15.5), start.AddHours(17)),
                new(start.AddHours(20), end),
            ];
            int actual = 5760 - (int)(gaps.Sum(gap => (gap.ThroughUtc - gap.FromUtc).TotalSeconds) / 15);
            vm.Datasets.Add(new(index.ToString("x64"), $"AAPL-{index}.json", "Robinhood", "aapl", "AAPL",
                date.AddDays(index), 15, "split", "robinhood-split-unversioned", "24_5", end,
                new(start, end, start, end, 5760, actual, false, true, gaps)));
        }
        foreach (LibraryDaySummary summary in LibraryDaySummary.Create(vm.Datasets)) vm.LibraryDays.Add(summary);
        return vm.LibraryDays.Single(summary => summary.TradingDate == date);
    }
}
