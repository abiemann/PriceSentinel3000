using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PriceSentinel3000.App.Dialogs;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(CollectionJobStatus.Pending, 53.39, false, "53.39%", "Queued.")]
    [InlineData(CollectionJobStatus.Downloading, 75d, false, "75%", "In progress.")]
    [InlineData(CollectionJobStatus.Partial, 99.5, false, "99.5%", "Saved with gaps.")]
    [InlineData(CollectionJobStatus.Complete, 50d, true, "50%", "Saved so far.")]
    [InlineData(CollectionJobStatus.Complete, 100d, false, "100%", "Complete.")]
    [InlineData(CollectionJobStatus.Complete, null, false, "--", "Complete.")]
    [InlineData(CollectionJobStatus.Complete, null, true, "--", "Saved so far.")]
    [InlineData(CollectionJobStatus.Pending, null, false, "--", "Queued.")]
    [InlineData(CollectionJobStatus.Partial, null, false, "--", "Saved with gaps.")]
    [InlineData(CollectionJobStatus.Unavailable, null, false, "0%", "Data unavailable.")]
    [InlineData(CollectionJobStatus.Failed, 75d, false, "0%", "Failed.")]
    public void DownloadCoverage_StateShowsSavedPercentageAndDetailsIdentifyOutcome(
        CollectionJobStatus status, double? coverage, bool today, string state, string detailsPrefix)
    {
        var row = new DownloadJobViewModel(new()
        {
            Symbol = "AAPL", SessionDate = new(2026, 9, 9), SessionBounds = "24_5", Status = status,
            SavedCoveragePercent = coverage is { } saved ? (decimal)saved : null,
            RequestedThroughUtc = today ? new(2026, 9, 9, 16, 0, 0, TimeSpan.Zero) : null,
        });

        Assert.Equal(state, row.StateText);
        Assert.StartsWith(detailsPrefix, row.DetailsText);
        if (status == CollectionJobStatus.Failed)
        {
            Assert.Contains("Previously saved candles are kept", row.DetailsText);
            Assert.Contains("failed download attempt", row.StateToolTip);
        }
        else
        {
            Assert.Contains("full day's", row.StateToolTip);
            Assert.Contains("Market closures are excluded", row.StateToolTip);
        }
    }

    [Fact]
    public void DownloadCoverage_FailureAndRetryAreDistinctFromUnavailableData()
    {
        var failed = new DownloadJobViewModel(new()
        {
            Status = CollectionJobStatus.Failed, Error = "Internet connection was interrupted.", SavedCoveragePercent = 45,
        });
        var retrying = new DownloadJobViewModel(new()
        {
            Status = CollectionJobStatus.Pending, Error = "Internet connection was interrupted.", SavedCoveragePercent = 45,
            RetryAfterUtc = new(2026, 9, 9, 18, 0, 0, TimeSpan.Zero),
        });
        var unavailable = new DownloadJobViewModel(new()
        {
            Status = CollectionJobStatus.Unavailable, Error = "The broker returned no 15-second candles.",
        });

        Assert.Equal("0%", failed.StateText);
        Assert.StartsWith("Failed. Internet connection was interrupted.", failed.DetailsText);
        Assert.Contains("Previously saved candles are kept", failed.DetailsText);
        Assert.Equal("45%", retrying.StateText);
        Assert.StartsWith("Retry waiting. Internet connection was interrupted.", retrying.DetailsText);
        Assert.Equal("0%", unavailable.StateText);
        Assert.StartsWith("Data unavailable.", unavailable.DetailsText);
        Assert.DoesNotContain("Failed", unavailable.DetailsText);
    }

    [Fact]
    public void DownloadCoverage_SavedCoverageChangesNotifyWithoutChangingCheckedProgress()
    {
        var job = new CollectionJob
        {
            Symbol = "AAPL", SessionDate = new(2026, 9, 9), SessionBounds = "24_5",
            SavedCoveragePercent = 50, NextGapFromUtc = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero),
        };
        var row = new DownloadJobViewModel(job);
        var properties = new List<string?>();
        row.PropertyChanged += (_, change) => properties.Add(change.PropertyName);

        Assert.Equal(25d, row.CheckedProgressPercent);
        Assert.False(row.Update(job with { SavedCoveragePercent = 65.25m }));
        Assert.Equal("65.25%", row.StateText);
        Assert.Equal(25d, row.CheckedProgressPercent);
        Assert.Contains(properties, name => string.IsNullOrEmpty(name) || name == nameof(DownloadJobViewModel.StateText));
    }

    [Fact]
    public Task DownloadCoverage_GridDisplaysLivePercentAndSortsStateNumerically() => host.RunAsync(async () =>
    {
        var job = new CollectionJob
        {
            Symbol = "AAPL", Status = CollectionJobStatus.Partial, SavedCoveragePercent = 53.39m,
            Error = "All missing sections were checked.",
        };
        await using var fixture = new ProgressFixture(job);
        DataRetentionViewModel vm = fixture.ViewModel;
        DownloadJobViewModel row = Assert.Single(vm.Jobs);
        var dialog = new DataRetentionDialog
        {
            DataContext = vm, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000,
        };
        try
        {
            dialog.Show();
            ((TabControl)dialog.FindName("RetentionTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)dialog.FindName("DownloadJobsGrid");
            var state = Assert.IsType<DataGridTextColumn>(Assert.Single(grid.Columns, column => Equals(column.Header, "State")));
            var details = Assert.IsType<DataGridTextColumn>(Assert.Single(grid.Columns, column => Equals(column.Header, "Details")));
            var stateCell = Assert.IsType<TextBlock>(state.GetCellContent(row));
            var detailsCell = Assert.IsType<TextBlock>(details.GetCellContent(row));
            Assert.Equal("53.39%", stateCell.Text);
            Assert.Equal(row.StateToolTip, stateCell.ToolTip);
            Assert.StartsWith("Saved with gaps.", detailsCell.Text);
            Assert.True(state.CanUserSort);
            Assert.Equal(nameof(DownloadJobViewModel.StateCoveragePercent), state.SortMemberPath);
            Assert.False(details.CanUserSort);

            row.Update(job with { SavedCoveragePercent = 70.25m });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("70.25%", stateCell.Text);
        }
        finally { dialog.Close(); }
    });
}
