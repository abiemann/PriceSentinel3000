using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.App.Views;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData("2026-09-04", "24_5", "2026-09-04T10:00:00Z", null, 30d)]
    [InlineData("2026-09-08", "24_5", "2026-09-08T10:00:00Z", null, 25d)]
    [InlineData("2026-09-08", "24_5", "2026-09-08T10:00:00Z", "2026-09-08T16:00:00Z", 50d)]
    [InlineData("2026-09-08", "24_5", "2026-09-08T17:00:00Z", "2026-09-08T16:00:00Z", 100d)]
    [InlineData("2026-09-13", "24_5", "2026-09-14T02:00:00Z", null, 50d)]
    [InlineData("2026-09-13", "24_5", "2026-09-13T23:00:00Z", null, 0d)]
    [InlineData("2026-11-27", "24_5", "2026-11-27T13:30:00Z", null, 50d)]
    [InlineData("2026-09-04", "regular", "2026-09-04T16:45:00Z", null, 50d)]
    public void DownloadProgress_CheckedFractionUsesActualTradingWindowsAndSnapshotCutoff(
        string day, string bounds, string cursor, string? cutoff, double expected)
    {
        var row = new DownloadJobViewModel(new()
        {
            Symbol = "NFLX", SessionDate = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            SessionBounds = bounds, NextGapFromUtc = DateTimeOffset.Parse(cursor, CultureInfo.InvariantCulture),
            RequestedThroughUtc = cutoff is null ? null : DateTimeOffset.Parse(cutoff, CultureInfo.InvariantCulture),
        });

        Assert.Equal(expected, row.CheckedProgressPercent);
        Assert.Contains("requested trading time checked", row.DetailsText);
        Assert.DoesNotContain("coverage", row.DetailsText);
        Assert.Equal(CollectionJobStatus.Pending, row.Status);
        Assert.Null(row.ActualSourceIntervalSeconds);
    }

    [Fact]
    public Task DownloadProgress_CursorAdvanceUpdatesHeldRequestRangeAndLastProgressWithoutFinishingOrReplacingRow() => host.RunAsync(async () =>
    {
        var clock = new TestClock { Now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero) };
        await using var fixture = new DownloadPumpFixture(clock: clock,
            jobs: [new() { Symbol = "NFLX", SessionBounds = "24_5" }]);
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadJobViewModel row = Assert.Single(vm.Jobs);
        Assert.Equal(0d, row.CheckedProgressPercent);
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        vm.Jobs.CollectionChanged += (_, change) => collectionChanges.Add(change.Action);
        var progressUpdates = new List<double>();
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(DataRetentionViewModel.DownloadProgressPercent))
                progressUpdates.Add(vm.DownloadProgressPercent);
        };
        await using var workspace = new TestWorkspace();
        workspace.ViewModel.DataRetention = vm;
        var header = new AppHeaderView { DataContext = workspace.ViewModel };
        var headerWindow = new Window
        {
            Content = header, Width = 1500, Height = 110, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        headerWindow.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var headerProgress = (ProgressBar)header.FindName("HeaderDownloadProgress");
        Assert.Equal(0d, headerProgress.Value);
        var properties = new List<string?>();
        row.PropertyChanged += (_, change) => properties.Add(change.PropertyName);
        fixture.Provider.HoldCall = 2;
        Task downloading = vm.CheckDownloadsAsync();
        try
        {
            await fixture.Provider.HeldRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CollectionActivity activity = Assert.IsType<CollectionActivity>(fixture.Collector.Activity);
            Assert.Equal(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero), activity.FromUtc);
            Assert.Equal(new DateTimeOffset(2026, 9, 4, 16, 0, 0, TimeSpan.Zero), activity.ThroughUtc);
            Assert.Equal("Working", vm.DownloadState);
            Assert.Equal(30d, row.CheckedProgressPercent);
            Assert.Equal(30d, vm.DownloadProgressPercent);
            Assert.Contains(30d, progressUpdates);
            Assert.True(headerProgress.IsVisible);
            Assert.Equal(30d, headerProgress.Value);
            Assert.False(headerProgress.IsIndeterminate);
            Assert.Contains("06:00:00–12:00:00 Eastern", vm.DownloadDetail);
            Assert.Contains("30% of requested trading time checked", vm.DownloadDetail);
            Assert.DoesNotContain("coverage", vm.DownloadDetail);
            Assert.Contains("Current step just started", vm.DownloadTiming);
            Assert.Contains("Last progress just now", vm.DownloadTiming);
            Assert.Equal(0, vm.DownloadProcessed);
            Assert.Same(row, Assert.Single(vm.Jobs));
            Assert.Empty(collectionChanges);
            Assert.NotEmpty(properties);

            clock.Now = clock.Now.AddSeconds(5);
            await vm.CheckDownloadsAsync();
            Assert.Contains("Current step: 5s", vm.DownloadTiming);
            Assert.Contains("Last progress 5s ago", vm.DownloadTiming);
            Assert.Contains("06:00:00–12:00:00 Eastern", vm.DownloadDetail);
            Assert.Equal(2, fixture.Provider.Requests.Count);
            Assert.Equal(30d, vm.DownloadProgressPercent);
            Assert.Equal(30d, headerProgress.Value);
            Assert.All(progressUpdates, value => Assert.InRange(value, 0d, 100d));
            Assert.Same(row, Assert.Single(vm.Jobs));
            Assert.Empty(collectionChanges);
        }
        finally
        {
            headerWindow.Close();
            await vm.PauseDownloadsCommand.ExecuteAsync();
            await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task DownloadProgress_HeldFirstRequestShowsRangeAndZeroCheckedWithoutClaimingSavedCoverage() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture(new CollectionJob { Symbol = "NFLX", SessionBounds = "24_5" });
        fixture.Connected = true;
        fixture.Provider.HoldDownloads = true;
        DataRetentionViewModel vm = fixture.ViewModel;
        Task downloading = vm.CheckDownloadsAsync();
        try
        {
            await fixture.Provider.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains("00:00:00–06:00:00 Eastern", vm.DownloadDetail);
            Assert.Contains("0% of requested trading time checked", vm.DownloadDetail);
            Assert.Contains("Current step just started", vm.DownloadTiming);
            Assert.Equal(0d, Assert.Single(vm.Jobs).CheckedProgressPercent);
            Assert.Equal(0, vm.DownloadProcessed);
        }
        finally
        {
            await vm.PauseDownloadsCommand.ExecuteAsync();
            await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });

    [Fact]
    public Task DownloadProgress_ScheduleHelpAndSavedStatusFollowSavedAutomaticState() => host.RunAsync(async () =>
    {
        await using var fixture = new ProgressFixture();
        DataRetentionViewModel vm = fixture.ViewModel;
        Assert.StartsWith("Automatic downloads are off.", vm.ScheduleHelp);
        vm.AutomaticDownloadsEnabled = true;
        Assert.StartsWith("Automatic downloads are off.", vm.ScheduleHelp);
        await vm.SaveScheduleAsync();
        Assert.StartsWith("Runs at your saved daily time", vm.ScheduleHelp);
        Assert.Contains("Automatic downloads collect", vm.Status);

        vm.AutomaticDownloadsEnabled = false;
        Assert.StartsWith("Runs at your saved daily time", vm.ScheduleHelp);
        await vm.SaveScheduleAsync();
        Assert.StartsWith("Automatic downloads are off.", vm.ScheduleHelp);
        Assert.Contains("Automatic downloads are off.", vm.Status);
        Assert.Contains("Download gaps now remains available.", vm.Status);
        Assert.Equal(0, fixture.Provider.DownloadCalls);
    });
}
