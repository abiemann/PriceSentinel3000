using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ClearDownloadQueue_EmptyQueueCannotBeCleared() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        Assert.False(fixture.ViewModel.ClearDownloadQueueCommand.CanExecute(null));
    });

    [Fact]
    public Task ClearDownloadQueue_RemovesAllFinishedStatesIncludingHiddenDiscoveryRows() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(
            new CollectionJob { Symbol = "AAPL", Status = CollectionJobStatus.Complete },
            new CollectionJob { Symbol = "AMD", Status = CollectionJobStatus.Partial },
            new CollectionJob { Symbol = "NVDA", Status = CollectionJobStatus.Failed },
            new CollectionJob { Symbol = "NFLX", Status = CollectionJobStatus.Unavailable, IsAvailabilityProbe = true });
        var vm = fixture.ViewModel;
        Assert.Equal(4, vm.DownloadTotal);
        Assert.Equal(3, vm.VisibleJobs.Count);
        Assert.True(vm.ClearDownloadQueueCommand.CanExecute(null));

        await vm.ClearDownloadQueueCommand.ExecuteAsync();

        Assert.Empty(vm.Collector.State.Jobs);
        Assert.Empty(vm.Jobs);
        Assert.Empty(vm.VisibleJobs);
        Assert.Equal("Idle", vm.DownloadState);
        Assert.Equal(0, vm.DownloadProgressPercent);
        Assert.False(vm.ClearDownloadQueueCommand.CanExecute(null));
        Assert.True(vm.DownloadNowCommand.CanExecute(null));
        Assert.True(vm.ForcedDownloadCommand.CanExecute(null));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task ClearDownloadQueue_QueuedWorkPreventsClearingEvenWhileIdle(bool automatic, bool delayed) => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(
            new CollectionJob { Symbol = "AAPL", Status = CollectionJobStatus.Complete },
            new CollectionJob
            {
                Symbol = "NFLX", IsAutomatic = automatic, IsAvailabilityProbe = true, AvailabilityCheckPending = true,
                RetryAfterUtc = delayed ? DateTimeOffset.Parse("2026-09-08T20:00:00Z") : null,
            });
        var vm = fixture.ViewModel;
        Assert.False(vm.IsBusy);
        Assert.Single(vm.VisibleJobs);
        Assert.False(vm.ClearDownloadQueueCommand.CanExecute(null));

        await vm.ClearDownloadQueueCommand.ExecuteAsync();

        Assert.Equal(2, vm.Collector.State.Jobs.Count);
    });

    [Fact]
    public Task ClearDownloadQueue_ButtonEnablesAfterCompletionAndKeepsSavedHistory() => host.RunAsync(async () =>
    {
        await using var fixture = new RetentionFixture();
        await fixture.SaveSingleSymbol();
        fixture.Provider.HoldDownloads = true;
        var vm = fixture.ViewModel;
        Task download = StartQueuedRetentionDownloadsAsync(vm);
        var dialog = new DataRetentionDialog { DataContext = vm, ShowActivated = false };
        try
        {
            await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dialog.Show();
            ((TabItem)dialog.FindName("ScheduleDownloadsTab")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var clear = (Button)dialog.FindName("ClearDownloadQueueButton");
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            Assert.False(clear.IsEnabled);

            await vm.PauseDownloadsCommand.ExecuteAsync();
            await download.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(vm.IsDownloadActive);
            Assert.False(clear.IsEnabled);
            Assert.Equal(CollectionJobStatus.Pending, Assert.Single(vm.Jobs).Status);

            fixture.Provider.HoldDownloads = false;
            await vm.PauseDownloadsCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(clear.IsEnabled);
            Assert.Single(grid.Items);
            var saved = Assert.Single(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets);
            string savedPath = Path.Combine(fixture.LibraryRoot, saved.RelativePath);
            byte[] candles = await File.ReadAllBytesAsync(savedPath);

            await vm.ClearDownloadQueueCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.False(clear.IsEnabled);
            Assert.Empty(grid.Items);
            Assert.Empty(new JsonCollectionStateStore(fixture.StatePath).Load().Jobs);
            Assert.Equal(candles, await File.ReadAllBytesAsync(savedPath));
            Assert.Equal(saved.DatasetHash, Assert.Single(new JsonMarketDataLibrary(fixture.LibraryRoot).Scan().Datasets).DatasetHash);
            Assert.Single(vm.Collector.State.Settings.Lists);
            Assert.Equal("Ready to download", vm.DownloadHeading);
        }
        finally
        {
            dialog.Close();
            if (!download.IsCompleted)
            {
                vm.CancelDownloadsCommand.Execute(null);
                await download.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    });
}
