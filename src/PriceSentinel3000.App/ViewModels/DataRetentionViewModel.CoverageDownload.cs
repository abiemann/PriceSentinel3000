using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class DataRetentionViewModel
{
    private IReadOnlyList<Guid> _coverageDownloadIds = [];
    private string _coverageDownloadRevision = "";
    private Task? _coverageDownloadTask;
    private bool _isCoverageDownloading;
    private string _coverageDownloadStatus = "";

    public bool CanDownloadCoverage => !_disposed && !IsBusy && !HasDownloadWork;
    public bool IsCoverageDownloading => _isCoverageDownloading;
    public string CoverageDownloadStatus
    {
        get => _coverageDownloadStatus;
        private set { _coverageDownloadStatus = value; Changed(); }
    }
    public event EventHandler? CoverageDownloadUpdated;

    public Task DownloadCoverageAsync(LibraryCoverageTimeline timeline, LibraryCoverageBlock selected,
        string? libraryRootPath = null)
    {
        if (!CanDownloadCoverage || timeline.GetConnectedMissingRange(selected, _clock.GetUtcNow()) is not { } range)
            return Task.CompletedTask;
        // The request belongs to the background collector, not the popup lifetime.
        _coverageDownloadTask = ExecuteAsync(async () =>
        {
            _isCoverageDownloading = true;
            Changed(nameof(IsCoverageDownloading));
            CoverageDownloadStatus = "Downloading connected missing blocks…";
            try
            {
                _coverageDownloadRevision = "";
                _coverageDownloadIds = await Collector.QueueRangeAsync(timeline.Symbol,
                    range.FromUtc, range.ThroughUtc, _lifetime.Token, libraryRootPath);
                if (_coverageDownloadIds.Count == 0)
                {
                    CoverageDownloadStatus = "No completed trading candles to download in this range.";
                    return;
                }
                await RunDownloadsAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                CoverageDownloadStatus = $"Download could not finish. {exception.Message} Saved data is kept.";
                throw;
            }
            finally
            {
                _isCoverageDownloading = false;
                Changed(nameof(IsCoverageDownloading));
            }
        });
        return _coverageDownloadTask;
    }

    private async Task RefreshCoverageDownloadAsync()
    {
        if (_disposed || _coverageDownloadIds.Count == 0) return;
        CollectionJob[] jobs = Collector.State.Jobs.Where(job => _coverageDownloadIds.Contains(job.Id)).ToArray();
        string revision = string.Join("|", jobs.Select(job =>
            $"{job.Id}/{job.Status}/{job.LastAttemptAtUtc:O}/{string.Join(',', job.DatasetHashes)}"));
        if (_coverageDownloadRevision == revision) return;
        _coverageDownloadRevision = revision;
        try
        {
            string currentRoot = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(Collector.State.Settings.LibraryRootPath));
            foreach (string root in jobs.Select(job => System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(job.LibraryRootPath))).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase)) await ScanLibraryAsync();
                else await Collector.ScanLibraryAsync(_libraryFactory(root), cancellationToken: _lifetime.Token);
            }
            CoverageDownloadStatus = jobs.Any(job => job.Status == CollectionJobStatus.Failed)
                ? "Download failed. Saved data is kept; select DOWNLOAD to retry."
                : jobs.Any(job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading)
                    ? _downloadsPaused ? "Download paused. Saved data and queued work are kept."
                        : "Download queued for retry or reconnection. Saved data is kept."
                    : jobs.Any(job => job.Status == CollectionJobStatus.Unavailable)
                        ? "Data is unavailable from Robinhood for this range. Saved data is kept."
                        : jobs.Any(job => job.Status == CollectionJobStatus.Partial)
                            ? "Available candles saved; some selected gaps remain."
                            : "Download complete. Saved coverage refreshed.";
            CoverageDownloadUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CoverageDownloadStatus = $"Coverage could not be refreshed. {exception.Message}";
        }
        if (jobs.All(job => job.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading)))
            _coverageDownloadIds = [];
    }
}
