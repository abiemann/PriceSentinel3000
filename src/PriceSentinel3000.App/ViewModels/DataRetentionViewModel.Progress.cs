using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

/// <summary>Stable row identity preserves selection and scrolling when a job changes.</summary>
public sealed class DownloadJobViewModel(CollectionJob job) : INotifyPropertyChanged
{
    private CollectionJob _job = job;
    public Guid Id => _job.Id;
    public string Symbol => _job.Symbol;
    public DateOnly SessionDate => _job.SessionDate;
    public CollectionJobStatus Status => _job.Status;
    public int? ActualSourceIntervalSeconds => _job.ActualSourceIntervalSeconds;
    public string? Error => _job.Error;
    public bool IsAutomatic => _job.IsAutomatic;
    public DateTimeOffset? RetryAfterUtc => _job.RetryAfterUtc;
    public string StateText => Status switch
    {
        CollectionJobStatus.Pending => RetryAfterUtc is null ? "Queued" : "Retry waiting",
        CollectionJobStatus.Downloading => "In progress",
        CollectionJobStatus.Complete => "Complete",
        CollectionJobStatus.Partial => "Saved with gaps",
        CollectionJobStatus.Unavailable => "Unavailable",
        _ => "Failed",
    };
    public string DetailsText => !string.IsNullOrEmpty(Error) ? Error : Status switch
    {
        CollectionJobStatus.Pending => "Waiting for its turn in the download queue.",
        CollectionJobStatus.Downloading => "Checking or downloading genuine 15-second history.",
        CollectionJobStatus.Complete => ActualSourceIntervalSeconds is { } interval
            ? $"Complete {interval}-second coverage saved on disk." : "Complete coverage saved on disk.",
        _ => "See the download status for details.",
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool Update(CollectionJob next)
    {
        bool progress = (Status != next.Status || _job.LastAttemptAtUtc != next.LastAttemptAtUtc) &&
            next.Status is CollectionJobStatus.Complete or CollectionJobStatus.Partial or CollectionJobStatus.Unavailable or CollectionJobStatus.Failed;
        bool changed = Status != next.Status || ActualSourceIntervalSeconds != next.ActualSourceIntervalSeconds ||
            Error != next.Error || IsAutomatic != next.IsAutomatic || RetryAfterUtc != next.RetryAfterUtc;
        _job = next;
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return progress;
    }
}

public sealed partial class DataRetentionViewModel
{
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _progressTimer;
    private readonly Dictionary<Guid, DownloadJobViewModel> _jobRows = [];
    private bool _started;
    private bool _connecting;
    private bool _downloadsPaused;
    private bool _loadedJobRows;
    private DateTimeOffset? _nextDownloadCheckAt;
    private DateTimeOffset? _lastDownloadProgressAt;
    private string? _collectionError;
    private string _downloadState = "Idle";
    private string _downloadHeading = "Ready to download";
    private string _downloadDetail = "Choose saved equities and dates, then select Download now.";
    private string _downloadTiming = "";

    public bool CanEditPlan => !IsBusy && !_disposed;
    public bool IsConnecting => _connecting;
    public bool IsDownloadActive => _connecting || Collector.IsBusy;
    public string DownloadState => _downloadState;
    public string DownloadHeading => _downloadHeading;
    public string DownloadDetail => _downloadDetail;
    public string DownloadTiming => _downloadTiming;
    public int DownloadTotal => Jobs.Count;
    public int DownloadProcessed => Jobs.Count(j => j.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading));
    public double DownloadProgressPercent => DownloadTotal == 0 ? 0 : 100d * DownloadProcessed / DownloadTotal;
    public string DownloadInteractionHint => _downloadsPaused
        ? "Downloads stay paused until you resume or restart the app. Saved files are kept. You can browse, edit settings, or close this window."
        : "You can switch tabs, scroll, edit drafts, or close this window while downloading. Save actions wait until downloads are idle. Keep PriceSentinel open.";
    public string PauseDownloadsLabel => _downloadsPaused ? "RESUME DOWNLOADS" : "PAUSE DOWNLOADS";
    public AsyncRelayCommand PauseDownloadsCommand { get; }
    public bool HasScheduleChanges
    {
        get
        {
            CollectionSettings saved = Collector.State.Settings;
            return AutomaticDownloadsEnabled != saved.AutomaticDownloadsEnabled ||
                !TimeOnly.TryParseExact(DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time) ||
                time != saved.DailyDownloadTime || TimeZoneId != saved.TimeZoneId || SessionBounds != saved.SessionBounds ||
                !string.Equals(LibraryRootPath, saved.LibraryRootPath, StringComparison.OrdinalIgnoreCase);
        }
    }
    public string ScheduleChangesText => HasScheduleChanges
        ? "UNSAVED CHANGES. Downloads still use the saved schedule, session, and folder. Select Save schedule & folder to apply this draft."
        : "These settings are saved. Manual downloads work even when the automatic schedule is off.";

    private void ScheduleDraftChanged()
    {
        Changed(nameof(HasScheduleChanges));
        Changed(nameof(ScheduleChangesText));
    }

    public async Task CheckDownloadsAsync()
    {
        if (_disposed) return;
        if (IsBusy || _downloadsPaused) { ScheduleNextDownloadCheck(); RefreshDownloadPresentation(); return; }
        Task task = PollAsync();
        _pollTask = task;
        RefreshState();
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _collectionError = exception.Message;
            Status = $"Collection needs attention: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_pollTask, task)) _pollTask = null;
            RefreshState();
        }
    }

    private bool CanToggleDownloadPause() => !_disposed && (_downloadsPaused ? !IsBusy :
        _downloadCancellation is not null || Jobs.Any(j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading));

    private async Task ToggleDownloadPauseAsync()
    {
        if (!_downloadsPaused)
        {
            _downloadsPaused = true;
            _downloadCancellation?.Cancel();
            Status = "Pausing downloads. Saved files and queued work are kept; Resume downloads continues the queue.";
            RefreshState();
        }
        else
        {
            _downloadsPaused = false;
            await ExecuteAsync(RunDownloadsAsync);
        }
    }

    private void ScheduleNextDownloadCheck()
    {
        _nextDownloadCheckAt = _clock.GetUtcNow().AddSeconds(30);
        if (!_started || _disposed) return;
        _timer.Stop();
        _timer.Start();
    }

    private void RefreshJobRows()
    {
        CollectionJob[] jobs = Collector.State.Jobs.OrderByDescending(j => j.QueuedAtUtc).ToArray();
        HashSet<Guid> ids = jobs.Select(j => j.Id).ToHashSet();
        for (int index = Jobs.Count - 1; index >= 0; index--)
            if (!ids.Contains(Jobs[index].Id)) { _jobRows.Remove(Jobs[index].Id); Jobs.RemoveAt(index); }
        for (int index = 0; index < jobs.Length; index++)
        {
            CollectionJob job = jobs[index];
            if (_jobRows.TryGetValue(job.Id, out DownloadJobViewModel? row))
            {
                if (row.Update(job)) _lastDownloadProgressAt = _clock.GetUtcNow();
            }
            else
            {
                row = new(job);
                _jobRows.Add(job.Id, row);
                Jobs.Insert(Math.Min(index, Jobs.Count), row);
                if (_loadedJobRows && job.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading))
                    _lastDownloadProgressAt = _clock.GetUtcNow();
            }
        }
        _loadedJobRows = true;
    }

    private void RefreshDownloadPresentation()
    {
        if (_disposed) return;
        DateTimeOffset now = _clock.GetUtcNow();
        CollectionState state = Collector.State;
        CollectionActivity? activity = Collector.Activity;
        CollectionJob[] pending = state.Jobs.Where(j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading).ToArray();
        CollectionJob[] eligible = pending.Where(j => !j.IsAutomatic || state.Settings.AutomaticDownloadsEnabled).ToArray();
        int attention = state.Jobs.Count(j => j.Status is CollectionJobStatus.Partial or CollectionJobStatus.Unavailable or CollectionJobStatus.Failed);
        string status, heading, detail;
        if (_downloadsPaused)
        {
            status = "Paused";
            heading = IsDownloadActive ? "Pausing downloads…" : "Downloads paused";
            detail = $"{pending.Length} queued date/equity downloads are kept. Resume downloads continues them.";
        }
        else if (_connecting)
        {
            status = "Working"; heading = "Connecting to Robinhood…";
            detail = "Waiting for authorization before downloading. Complete the browser login if one opens.";
        }
        else if (activity is not null)
        {
            status = "Working";
            string target = activity.Symbol is null ? "" : $"{activity.Symbol} · {activity.SessionDate:yyyy-MM-dd}";
            (heading, detail) = activity.Stage switch
            {
                "CheckingSchedule" => ("Checking saved coverage…", "Finding missing sessions for the saved equity lists."),
                "CheckingLocalHistory" => (activity.Symbol is null ? "Checking local library…" : $"Checking {target}",
                    "Reading saved candles before requesting missing 15-second data."),
                "WaitingForRateLimit" => ($"Waiting briefly · {target}", "Spacing requests to the broker. Downloads will continue automatically."),
                "Saving" => ($"Saving {target}", "Validating and writing the returned candles to the local library."),
                _ => ($"Downloading {target}", "Waiting for Robinhood to return 15-second candles. This request is still active."),
            };
        }
        else if (_collectionError is not null)
        {
            status = "Attention"; heading = "Downloads need attention"; detail = _collectionError;
        }
        else if (eligible.Length > 0)
        {
            status = "Waiting";
            if (!_isConnected())
            {
                heading = "Waiting for Robinhood connection";
                detail = $"{eligible.Length} downloads are queued. Reconnect Robinhood to continue; background checks cannot open a login.";
            }
            else
            {
                bool retryWaiting = eligible.All(j => j.RetryAfterUtc > now);
                heading = retryWaiting ? "Waiting to retry" : "Waiting for the next download batch";
                detail = retryWaiting
                    ? $"The broker request will be retried after {eligible.Min(j => j.RetryAfterUtc)!.Value.ToLocalTime():HH:mm:ss}. Queued work is kept."
                    : $"{eligible.Length} date/equity downloads remain. A 30-second pause between batches limits broker requests.";
            }
        }
        else if (pending.Length > 0)
        {
            status = "Paused"; heading = "Automatic queue paused";
            detail = $"{pending.Length} automatic downloads are queued. Enable and save the automatic schedule, or use Download now for a manual request.";
        }
        else if (attention > 0)
        {
            status = "Attention"; heading = "Queue finished with items to review";
            detail = $"{attention} dates have gaps, unavailable data, or errors. See Details in the table; Retry missing checks them again.";
        }
        else if (state.Jobs.Count > 0)
        {
            status = "Complete"; heading = "All queued downloads complete";
            detail = "Completed history is saved in the local library. You can replay it or close this window.";
        }
        else
        {
            status = "Idle"; heading = "Ready to download";
            detail = "Save your equity list, choose dates, then select Download now.";
        }
        string timing = _lastDownloadProgressAt is { } last
            ? $"Last job finished {Elapsed(now - last)} ago ({last.ToLocalTime():HH:mm:ss})."
            : "No job has finished since this window's data was loaded.";
        if (activity is not null) timing = $"Current step: {Elapsed(now - activity.SinceUtc)}. " + timing;
        else if (!_downloadsPaused && eligible.Length > 0 && _started && _nextDownloadCheckAt is { } next)
            timing += $" Next queue check in {Math.Max(0, (int)Math.Ceiling((next - now).TotalSeconds))}s.";
        SetProgressText(ref _downloadState, status, nameof(DownloadState));
        SetProgressText(ref _downloadHeading, heading, nameof(DownloadHeading));
        SetProgressText(ref _downloadDetail, detail, nameof(DownloadDetail));
        SetProgressText(ref _downloadTiming, timing, nameof(DownloadTiming));
        Changed(nameof(IsConnecting)); Changed(nameof(IsDownloadActive)); Changed(nameof(DownloadTotal)); Changed(nameof(DownloadProcessed));
        Changed(nameof(DownloadProgressPercent)); Changed(nameof(DownloadInteractionHint)); Changed(nameof(PauseDownloadsLabel));
    }

    private static string Elapsed(TimeSpan time) => time.TotalMinutes >= 1
        ? $"{Math.Max(0, (int)time.TotalMinutes)}m {Math.Max(0, time.Seconds)}s" : $"{Math.Max(0, (int)time.TotalSeconds)}s";
    private void SetProgressText(ref string field, string value, string property)
    {
        if (field == value) return;
        field = value;
        Changed(property);
    }
}
