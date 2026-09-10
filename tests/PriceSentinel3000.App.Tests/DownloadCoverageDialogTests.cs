using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task DownloadCoverage_RowClickAndEnterOpenSavedDayWithSharedSelectionColors(int width, int height) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(
            new CollectionJob { Symbol = "SOFI", ProviderInstrumentId = "id-SOFI", SessionBounds = "24_5", Status = CollectionJobStatus.Partial },
            new CollectionJob { Symbol = "AAPL", ProviderInstrumentId = "id-AAPL", SessionBounds = "24_5", Status = CollectionJobStatus.Partial });
        DataRetentionViewModel vm = fixture.ViewModel;
        foreach (CollectionJob job in vm.Collector.State.Jobs)
        {
            DateTimeOffset from = CollectionSchedule.GetSessionWindow(job.SessionDate, "24_5").FromUtc;
            new JsonMarketDataLibrary(job.LibraryRootPath).Save(LibraryDownload(from, 15) with
            {
                Symbol = job.Symbol, InstrumentId = job.ProviderInstrumentId!, SessionBounds = "24_5",
            });
        }
        var dialog = CreateLocalLayoutDialog(vm, width, height);
        try
        {
            dialog.Show();
            var tab = (TabItem)dialog.FindName("ScheduleDownloadsTab");
            tab.IsSelected = true;
            await SettleLocalLayout(dialog);
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            var localGrid = (DataGrid)dialog.FindName("LocalLibraryGrid");
            var overlay = (Grid)dialog.FindName("LibraryCoverageOverlay");
            var content = (StackPanel)dialog.FindName("LibraryCoverageContent");
            var close = (Button)dialog.FindName("LibraryCoverageCloseButton");
            var success = (SolidColorBrush)dialog.FindResource("SuccessSurfaceBrush");
            var foreground = (SolidColorBrush)dialog.FindResource("PrimaryTextBrush");
            Assert.Same(localGrid.RowStyle, grid.RowStyle);
            Assert.Same(localGrid.CellStyle, grid.CellStyle);
            Assert.Null(grid.FocusVisualStyle);

            var headerClick = CoverageMouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            headerClick.Source = FindRetentionVisuals<DataGridColumnHeader>(grid).First();
            grid.RaiseEvent(headerClick);
            Assert.False(overlay.IsVisible);

            foreach (string symbol in new[] { "SOFI", "AAPL" })
            {
                DownloadJobViewModel job = vm.Jobs.Single(item => item.Symbol == symbol);
                grid.SelectedItem = job;
                grid.ScrollIntoView(job);
                grid.CurrentCell = new DataGridCellInfo(job, grid.Columns[0]);
                await SettleLocalLayout(dialog);
                DataGridRow row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(job));
                FindRetentionVisuals<DataGridCell>(row).First().Focus();
                await SettleLocalLayout(dialog);
                Assert.True(grid.IsKeyboardFocusWithin);
                AssertDownloadSelectionColors(row, success.Color, foreground.Color);

                if (symbol == "SOFI")
                {
                    var click = CoverageMouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
                    click.Source = row;
                    grid.RaiseEvent(click);
                    Assert.True(click.Handled);
                }
                else
                {
                    var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!,
                        Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    grid.RaiseEvent(enter);
                    Assert.True(enter.Handled);
                }
                await WaitForCoverageContent(dialog);

                Assert.True(overlay.IsVisible);
                LibraryCoverageTimeline timeline = Assert.IsType<LibraryCoverageTimeline>(content.DataContext);
                Assert.Equal(symbol, timeline.Symbol);
                Assert.Equal(job.SessionDate, timeline.Date);
                Assert.Equal(8, timeline.Blocks.Sum(block => block.SavedCandleCount));
                Assert.Equal($"{symbol} · {job.SessionDate:yyyy-MM-dd}", ((TextBlock)dialog.FindName("LibraryCoverageTitle")).Text);
                AssertInsideWindow(dialog, (Border)dialog.FindName("LibraryCoverageCard"));
                AssertInsideWindow(dialog, (LibraryCoverageTimelineControl)dialog.FindName("LibraryCoverageTimeline"));
                Assert.False(grid.IsKeyboardFocusWithin);
                AssertDownloadSelectionColors(row, success.Color, foreground.Color);
                if (symbol == "SOFI") CaptureLocalLayout(dialog, $"download-coverage-{width}x{height}.png");

                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleLocalLayout(dialog);
                Assert.False(overlay.IsVisible);
                Assert.True(dialog.IsVisible);
                Assert.Same(job, grid.SelectedItem);
                Assert.True(grid.IsKeyboardFocusWithin);
                AssertDownloadSelectionColors(row, success.Color, foreground.Color);
                tab.Focus();
                await SettleLocalLayout(dialog);
                Assert.False(grid.IsKeyboardFocusWithin);
                Assert.Same(job, grid.SelectedItem);
                AssertDownloadSelectionColors(row, success.Color, foreground.Color);
            }
            Assert.Equal(0, fixture.Provider.DownloadCalls);
            Assert.Equal(0, fixture.ConnectionCalls);
        }
        finally { dialog.Close(); }
    });

    private static void AssertDownloadSelectionColors(DataGridRow row, Color background, Color foreground)
    {
        Assert.True(row.IsSelected);
        Assert.Null(row.FocusVisualStyle);
        Assert.Equal(new Thickness(0), row.BorderThickness);
        Assert.Equal(background, Assert.IsType<SolidColorBrush>(row.Background).Color);
        Assert.Equal(foreground, Assert.IsType<SolidColorBrush>(row.Foreground).Color);
        DataGridCell[] cells = FindRetentionVisuals<DataGridCell>(row).ToArray();
        Assert.NotEmpty(cells);
        foreach (DataGridCell cell in cells)
        {
            Assert.True(cell.IsSelected);
            Assert.Null(cell.FocusVisualStyle);
            Assert.Equal(new Thickness(0), cell.BorderThickness);
            Assert.Equal(background, Assert.IsType<SolidColorBrush>(cell.Background).Color);
            Assert.Equal(foreground, Assert.IsType<SolidColorBrush>(cell.Foreground).Color);
        }
    }
}
