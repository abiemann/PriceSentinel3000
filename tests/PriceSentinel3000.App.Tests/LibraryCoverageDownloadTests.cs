using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PriceSentinel3000.App.Controls;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task LibraryCoverageDownload_FillsOnlyConnectedMissingBlocksAndRefreshesSavedInventory() =>
        host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryCoverageTimeline timeline = DownloadSelectionTimeline();
        Assert.True(vm.CanDownloadCoverage);

        await vm.DownloadCoverageAsync(timeline, timeline.Blocks[2]);

        HistoricalDataRequest request = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(timeline.Blocks[1].FromUtc, request.FromUtc);
        Assert.Equal(timeline.Blocks[3].ThroughUtc, request.ThroughUtc);
        Assert.Equal("NFLX", request.Symbol);
        Assert.Equal(15, request.SourceIntervalSeconds);
        Assert.Equal(CollectionJobStatus.Complete, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Equal(180, Assert.Single(vm.Datasets).Coverage.ActualCandleCount);
        Assert.Equal(180, Assert.Single(vm.LibraryDays).CandleCount);
        Assert.Contains("complete", vm.CoverageDownloadStatus.ToLowerInvariant());
        Assert.False(vm.IsCoverageDownloading);
        Assert.True(vm.CanDownloadCoverage);

        HistoricalDataQueryResult saved = new JsonMarketDataLibrary(fixture.LibraryRoot).Query(
            new("NFLX", request.FromUtc, request.ThroughUtc, SessionBounds: "24_5"));
        Assert.True(saved.Succeeded);
        Assert.True(saved.Coverage.Complete);
        Assert.Equal(180, saved.Candles.Count);
    });

    [Fact]
    public Task LibraryCoverageDownload_FormingBlockStopsAtTheLastCompletedNativeCandle() =>
        host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        LibraryCoverageTimeline timeline = DownloadSelectionTimeline();
        fixture.Clock.Now = timeline.Blocks[3].FromUtc.AddMinutes(3).AddSeconds(7);

        await fixture.ViewModel.DownloadCoverageAsync(timeline, timeline.Blocks[3]);

        HistoricalDataRequest request = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(timeline.Blocks[1].FromUtc, request.FromUtc);
        Assert.Equal(timeline.Blocks[3].FromUtc.AddMinutes(3), request.ThroughUtc);
        Assert.Equal(132, Assert.Single(fixture.ViewModel.Datasets).Coverage.ActualCandleCount);
    });

    [Fact]
    public Task LibraryCoverageDownload_HeldRequestPreventsDuplicatesAndPauseKeepsTheTargetQueued() =>
        host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryCoverageTimeline timeline = DownloadSelectionTimeline();
        Task downloading = vm.DownloadCoverageAsync(timeline, timeline.Blocks[2]);
        try
        {
            await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(vm.IsCoverageDownloading);
            Assert.False(vm.CanDownloadCoverage);
            Guid jobId = Assert.Single(fixture.Collector.State.Jobs).Id;

            await vm.DownloadCoverageAsync(timeline, timeline.Blocks[5]);
            Assert.Equal(1, fixture.Provider.DownloadCalls);
            Assert.Equal(jobId, Assert.Single(fixture.Collector.State.Jobs).Id);

            await vm.PauseDownloadsCommand.ExecuteAsync();
            await downloading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsCoverageDownloading);
            Assert.False(vm.CanDownloadCoverage);
            Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
            Assert.Empty(vm.Datasets);
            Assert.Contains("paused", vm.CoverageDownloadStatus.ToLowerInvariant());

            await vm.DownloadCoverageAsync(timeline, timeline.Blocks[5]);
            Assert.Equal(1, fixture.Provider.DownloadCalls);
            Assert.Equal(jobId, Assert.Single(fixture.Collector.State.Jobs).Id);
        }
        finally
        {
            vm.CancelDownloadsCommand.Execute(null);
            await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task LibraryCoverageDownload_EmptyResponseIsUnavailableAndCanBeExplicitlyRetried() =>
        host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        fixture.Provider.AvailableDates = [];
        DataRetentionViewModel vm = fixture.ViewModel;
        LibraryCoverageTimeline timeline = DownloadSelectionTimeline();

        await vm.DownloadCoverageAsync(timeline, timeline.Blocks[2]);

        Assert.Equal(CollectionJobStatus.Unavailable, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Empty(vm.Datasets);
        Assert.Contains("unavailable", vm.CoverageDownloadStatus.ToLowerInvariant());
        Assert.True(vm.CanDownloadCoverage);
        int emptyRequests = fixture.Provider.DownloadCalls;
        fixture.Provider.AvailableDates = [timeline.Date];

        await vm.DownloadCoverageAsync(timeline, timeline.Blocks[2]);

        Assert.True(fixture.Provider.DownloadCalls > emptyRequests);
        Assert.Equal(180, Assert.Single(vm.Datasets).Coverage.ActualCandleCount);
        Assert.Contains("complete", vm.CoverageDownloadStatus.ToLowerInvariant());
    });

    [Theory]
    [InlineData(1080, 790)]
    [InlineData(860, 620)]
    public Task LibraryCoverageDownload_ButtonAppearsOnlyForMissingInsideFullWidthPanelAtTopRight(int width, int height) =>
        host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        LibraryDaySummary day = AddCoverageDialogDatasets(fixture.ViewModel);
        var dialog = CreateLocalLayoutDialog(fixture.ViewModel, width, height);
        try
        {
            dialog.Show();
            ((TabItem)dialog.FindName("LocalLibraryTab")).IsSelected = true;
            await dialog.ShowLibraryCoverageAsync(day);
            var content = (StackPanel)dialog.FindName("LibraryCoverageContent");
            LibraryCoverageTimeline model = RenderingTimeline(24);
            content.DataContext = model;
            await SettleLocalLayout(dialog);
            var timeline = (LibraryCoverageTimelineControl)dialog.FindName("LibraryCoverageTimeline");
            var button = (Button)dialog.FindName("LibraryCoverageDownloadButton");
            var panel = (Border)dialog.FindName("LibraryCoverageBlockPanel");
            var details = (TextBlock)dialog.FindName("LibraryCoverageBlockDetails");
            var status = (TextBlock)dialog.FindName("LibraryCoverageDownloadStatus");
            Assert.False(button.IsVisible);

            foreach (LibraryCoverageBlock block in model.Blocks.Take(5))
            {
                SelectCoverageDownloadBlock(timeline, model, block);
                await SettleLocalLayout(dialog);
                Assert.Equal(block.State == LibraryCoverageBlockState.Missing, button.IsVisible);
                if (!button.IsVisible) continue;
                Assert.True(button.IsEnabled);
                AssertInsideWindow(dialog, button);
                Point panelPosition = panel.TranslatePoint(new Point(), content);
                Assert.Equal(0, panelPosition.X, 2);
                Assert.Equal(content.ActualWidth, panelPosition.X + panel.ActualWidth, 2);
                Assert.Equal(80, panel.ActualHeight);
                Rect buttonBounds = new(button.TranslatePoint(new Point(), panel), button.RenderSize);
                Rect detailsBounds = new(details.TranslatePoint(new Point(), panel), details.RenderSize);
                Rect statusBounds = new(status.TranslatePoint(new Point(), panel), status.RenderSize);
                Rect panelInnerBounds = new(panel.Padding.Left + panel.BorderThickness.Left,
                    panel.Padding.Top + panel.BorderThickness.Top,
                    panel.ActualWidth - panel.Padding.Left - panel.Padding.Right - panel.BorderThickness.Left - panel.BorderThickness.Right,
                    panel.ActualHeight - panel.Padding.Top - panel.Padding.Bottom - panel.BorderThickness.Top - panel.BorderThickness.Bottom);
                Assert.True(panelInnerBounds.Contains(buttonBounds));
                Assert.True(panelInnerBounds.Contains(detailsBounds));
                Assert.True(new Rect(new Point(), panel.RenderSize).Contains(statusBounds));
                Assert.Equal(panelInnerBounds.Right, buttonBounds.Right, 2);
                Assert.Equal(panelInnerBounds.Top, buttonBounds.Top, 2);
                Assert.False(buttonBounds.IntersectsWith(detailsBounds));
                Assert.False(buttonBounds.IntersectsWith(statusBounds));
                Assert.False(detailsBounds.IntersectsWith(statusBounds));
                Assert.Equal(panelInnerBounds.Left, statusBounds.Left, 2);
                Assert.InRange(Math.Abs(panelInnerBounds.Bottom - statusBounds.Bottom), 0, 1);
                Assert.True(button.ActualWidth <= 120);
                Assert.True(button.ActualHeight <= 40);
                CaptureLocalLayout(dialog, $"coverage-{width}x{height}-download.png");
            }
            Assert.Equal(0, fixture.Provider.DownloadCalls);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LibraryCoverageDownload_ButtonClickRefreshesTheOpenTimeline(bool changeWithKeyboard) => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        var dialog = CreateLocalLayoutDialog(fixture.ViewModel, 1080, 790);
        try
        {
            dialog.Show();
            LibraryCoverageTimeline model = DownloadSelectionTimeline();
            var day = new LibraryDaySummary("NFLX", model.Date, "15", "Robinhood", 0, 0, "");
            await dialog.ShowLibraryCoverageAsync(day);
            ((StackPanel)dialog.FindName("LibraryCoverageContent")).DataContext = model;
            await SettleLocalLayout(dialog);
            var timeline = (LibraryCoverageTimelineControl)dialog.FindName("LibraryCoverageTimeline");
            SelectCoverageDownloadBlock(timeline, model, model.Blocks[2]);
            await SettleLocalLayout(dialog);
            ((Button)dialog.FindName("LibraryCoverageDownloadButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (fixture.ViewModel.IsCoverageDownloading || fixture.ViewModel.Datasets.Count == 0 || timeline.Timeline == model)
                await Task.Delay(10, timeout.Token);
            await SettleLocalLayout(dialog);

            Assert.Equal(1, fixture.Provider.DownloadCalls);
            Assert.True(((Grid)dialog.FindName("LibraryCoverageOverlay")).IsVisible);
            Assert.Equal(180, timeline.Timeline!.Blocks.Sum(block => block.SavedCandleCount));
            Assert.Equal(180, Assert.Single(fixture.ViewModel.LibraryDays).CandleCount);
            var status = (TextBlock)dialog.FindName("LibraryCoverageDownloadStatus");
            var panel = (Border)dialog.FindName("LibraryCoverageBlockPanel");
            Rect panelBounds = new(panel.TranslatePoint(new Point(), dialog), panel.RenderSize);
            string completedStatus = status.Text;
            Assert.Contains("complete", completedStatus.ToLowerInvariant());
            LibraryCoverageTimeline refreshed = timeline.Timeline!;
            LibraryCoverageBlock downloadedBlock = Assert.IsType<LibraryCoverageBlock>(timeline.SelectedBlock);

            SelectCoverageDownloadBlock(timeline, refreshed, downloadedBlock);
            await SettleLocalLayout(dialog);
            Assert.Equal(completedStatus, status.Text);

            LibraryCoverageBlock nextBlock = refreshed.Blocks.First(block => block.FromUtc == downloadedBlock.ThroughUtc);
            if (changeWithKeyboard)
                timeline.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Right)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
            else
                SelectCoverageDownloadBlock(timeline, refreshed, nextBlock);
            await SettleLocalLayout(dialog);
            Assert.Equal(nextBlock.FromUtc, timeline.SelectedBlock!.FromUtc);
            Assert.Empty(status.Text);
            Assert.Equal(panelBounds, new Rect(panel.TranslatePoint(new Point(), dialog), panel.RenderSize));

            SelectCoverageDownloadBlock(timeline, refreshed, downloadedBlock);
            await SettleLocalLayout(dialog);
            Assert.Empty(status.Text);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task LibraryCoverageDownload_SelectingAnotherBlockDuringDownloadKeepsStatusClearAfterPause() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        fixture.Provider.HoldDownloads = true;
        var dialog = CreateLocalLayoutDialog(fixture.ViewModel, 1080, 790);
        try
        {
            dialog.Show();
            LibraryCoverageTimeline model = DownloadSelectionTimeline();
            await dialog.ShowLibraryCoverageAsync(new("NFLX", model.Date, "15", "Robinhood", 0, 0, ""));
            ((StackPanel)dialog.FindName("LibraryCoverageContent")).DataContext = model;
            await SettleLocalLayout(dialog);
            var timeline = (LibraryCoverageTimelineControl)dialog.FindName("LibraryCoverageTimeline");
            var status = (TextBlock)dialog.FindName("LibraryCoverageDownloadStatus");
            SelectCoverageDownloadBlock(timeline, model, model.Blocks[2]);
            await SettleLocalLayout(dialog);
            ((Button)dialog.FindName("LibraryCoverageDownloadButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettleLocalLayout(dialog);
            Assert.Contains("Downloading", status.Text);

            SelectCoverageDownloadBlock(timeline, model, model.Blocks[5]);
            await SettleLocalLayout(dialog);
            Assert.Empty(status.Text);
            await fixture.ViewModel.PauseDownloadsCommand.ExecuteAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (fixture.ViewModel.IsCoverageDownloading || timeline.Timeline == model)
                await Task.Delay(10, timeout.Token);
            await SettleLocalLayout(dialog);

            Assert.Contains("paused", fixture.ViewModel.CoverageDownloadStatus.ToLowerInvariant());
            Assert.Empty(status.Text);
            Assert.Equal(model.Blocks[5].FromUtc, timeline.SelectedBlock!.FromUtc);
            Assert.Equal(CollectionJobStatus.Pending, Assert.Single(fixture.Collector.State.Jobs).Status);
        }
        finally
        {
            fixture.ViewModel.CancelDownloadsCommand.Execute(null);
            dialog.Close();
        }
    });

    [Fact]
    public Task LibraryCoverageDownload_ClosingOverlayDoesNotCancelTheDownload() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol(queueDate: false);
        fixture.Provider.HoldDownloads = true;
        var dialog = CreateLocalLayoutDialog(fixture.ViewModel, 1080, 790);
        try
        {
            dialog.Show();
            LibraryCoverageTimeline model = DownloadSelectionTimeline();
            await dialog.ShowLibraryCoverageAsync(new("NFLX", model.Date, "15", "Robinhood", 0, 0, ""));
            ((StackPanel)dialog.FindName("LibraryCoverageContent")).DataContext = model;
            await SettleLocalLayout(dialog);
            var timeline = (LibraryCoverageTimelineControl)dialog.FindName("LibraryCoverageTimeline");
            SelectCoverageDownloadBlock(timeline, model, model.Blocks[2]);
            await SettleLocalLayout(dialog);
            var download = (Button)dialog.FindName("LibraryCoverageDownloadButton");
            download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SettleLocalLayout(dialog);
            Assert.False(download.IsEnabled);

            ((Button)dialog.FindName("LibraryCoverageCloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleLocalLayout(dialog);

            Assert.False(((Grid)dialog.FindName("LibraryCoverageOverlay")).IsVisible);
            Assert.True(fixture.ViewModel.IsCoverageDownloading);
            Assert.Equal(CollectionJobStatus.Downloading, Assert.Single(fixture.Collector.State.Jobs).Status);
            Assert.Equal(1, fixture.Provider.DownloadCalls);
            await fixture.ViewModel.PauseDownloadsCommand.ExecuteAsync();
        }
        finally
        {
            fixture.ViewModel.CancelDownloadsCommand.Execute(null);
            dialog.Close();
        }
    });
    private static void SelectCoverageDownloadBlock(LibraryCoverageTimelineControl control,
        LibraryCoverageTimeline timeline, LibraryCoverageBlock block)
    {
        double position = ((block.FromUtc - timeline.FromUtc).TotalSeconds +
            (block.ThroughUtc - block.FromUtc).TotalSeconds / 2) / (timeline.ThroughUtc - timeline.FromUtc).TotalSeconds;
        Assert.True(control.SelectBlockAt(new Point(28 + position * (control.ActualWidth - 56), 70)));
    }

    private static LibraryCoverageTimeline DownloadSelectionTimeline()
    {
        DateTimeOffset from = new(2026, 9, 4, 13, 0, 0, TimeSpan.Zero);
        LibraryCoverageBlockState[] states =
        [
            LibraryCoverageBlockState.Complete,
            LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Partial,
            LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Complete,
        ];
        LibraryCoverageBlock[] blocks = states.Select((state, index) => new LibraryCoverageBlock(
            from.AddMinutes(index * 15), from.AddMinutes((index + 1) * 15),
            state == LibraryCoverageBlockState.Complete ? 60 : state == LibraryCoverageBlockState.Partial ? 30 : 0,
            60, state, $"Block {index}: {state}")).ToArray();
        return new("NFLX", new(2026, 9, 4), "UTC", "13:00–14:45", "Test coverage", null,
            from, blocks[^1].ThroughUtc, blocks, []);
    }
}
