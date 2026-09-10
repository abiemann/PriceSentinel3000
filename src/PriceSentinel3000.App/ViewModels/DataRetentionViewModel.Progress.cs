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
    public bool IsAvailabilityProbe => _job.IsAvailabilityProbe;
    public bool AvailabilityCheckPending => _job.AvailabilityCheckPending;
    public DateTimeOffset? RequestedThroughUtc => _job.RequestedThroughUtc;
    public DateTimeOffset? NextGapFromUtc => _job.NextGapFromUtc;
    public double CheckedProgressPercent
    {
        get
        {
            if (_job.SessionBounds is not ("regular" or "extended" or "24_5")) return 0;
            long totalTicks = 0, checkedTicks = 0;
            foreach (CollectionSessionWindow window in CollectionSchedule.GetSessionWindows(SessionDate, _job.SessionBounds))
            {
                DateTimeOffset from = _job.RequestedFromUtc is { } start && start > window.FromUtc ? start : window.FromUtc;
                DateTimeOffset through = RequestedThroughUtc is { } cutoff && cutoff < window.ThroughUtc ? cutoff : window.ThroughUtc;
                if (through <= from) continue;
                totalTicks += (through - from).Ticks;
                DateTimeOffset cursor = NextGapFromUtc ?? (Status == CollectionJobStatus.Complete ? through : from);
                DateTimeOffset checkedThrough = cursor < through ? cursor : through;
                if (checkedThrough > from) checkedTicks += (checkedThrough - from).Ticks;
            }
            return totalTicks == 0 ? 0 : 100d * checkedTicks / totalTicks;
        }
    }
    public string CheckedProgressText => $"{CheckedProgressPercent:0.#}% of requested trading time checked.";
    public DateTimeOffset? RetryAfterUtc => _job.RetryAfterUtc;
    public decimal? SavedCoveragePercent => _job.SavedCoveragePercent;
    public decimal? StateCoveragePercent => Status is CollectionJobStatus.Failed or CollectionJobStatus.Unavailable ? 0m
        : SavedCoveragePercent is { } saved ? Math.Clamp(saved, 0m, 100m) : null;
    public string StateText => StateCoveragePercent is { } percent ? $"{percent:0.##}%" : "--";
    public string StateToolTip => Status == CollectionJobStatus.Failed
        ? "0% identifies this failed download attempt. Previously saved candles are kept; see Details for the error."
        : _job.RequestedFromUtc is not null && RequestedThroughUtc is not null
            ? "Genuine 15-second candles saved as a percentage of the selected trading range. Market closures and future time are excluded. -- means saved coverage has not been verified."
        : "Genuine 15-second candles saved as a percentage of the full day's available trading hours. Market closures are excluded. Today's remaining hours still count toward the full day. -- means saved coverage has not been verified.";
    public string StatusText => Status switch
    {
        CollectionJobStatus.Pending => RetryAfterUtc is null ? "Queued" : "Retry waiting",
        CollectionJobStatus.Downloading => "In progress",
        CollectionJobStatus.Complete => RequestedThroughUtc is null ? "Complete" : "Saved so far",
        CollectionJobStatus.Partial => "Saved with gaps",
        CollectionJobStatus.Unavailable => "Data unavailable",
        _ => "Failed",
    };
    public string DetailsText => $"{StatusText}. {DetailMessage}" + (Status == CollectionJobStatus.Failed
        ? " Previously saved candles are kept; 0% identifies the failed attempt." : "");
    private string DetailMessage => Status == CollectionJobStatus.Complete && RequestedThroughUtc is { } through
        ? $"Completed 15-second candles saved through {through.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Download again to collect newer candles."
        : !string.IsNullOrEmpty(Error) ? Error : Status switch
    {
        CollectionJobStatus.Pending => NextGapFromUtc is null ? "Waiting for its turn in the download queue."
            : $"{CheckedProgressText} Waiting for the next missing section.",
        CollectionJobStatus.Downloading => NextGapFromUtc is null ? "Checking or downloading genuine 15-second history."
            : $"{CheckedProgressText} Checking or downloading genuine 15-second history.",
        CollectionJobStatus.Unavailable when IsAvailabilityProbe => "The broker returned no 15-second history for this date.",
        CollectionJobStatus.Complete => ActualSourceIntervalSeconds is { } interval
            ? $"Complete {interval}-second coverage saved on disk." : "Complete coverage saved on disk.",
        _ => "See the download status for details.",
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool Update(CollectionJob next)
    {
        bool progress = next.NextGapFromUtc is { } cursor && (NextGapFromUtc is null || cursor > NextGapFromUtc) ||
            (Status != next.Status || _job.LastAttemptAtUtc != next.LastAttemptAtUtc) &&
            next.Status is CollectionJobStatus.Complete or CollectionJobStatus.Partial or CollectionJobStatus.Unavailable or CollectionJobStatus.Failed;
        bool changed = Status != next.Status || ActualSourceIntervalSeconds != next.ActualSourceIntervalSeconds ||
            Error != next.Error || IsAutomatic != next.IsAutomatic || RetryAfterUtc != next.RetryAfterUtc ||
            IsAvailabilityProbe != next.IsAvailabilityProbe || AvailabilityCheckPending != next.AvailabilityCheckPending ||
            _job.RequestedFromUtc != next.RequestedFromUtc || RequestedThroughUtc != next.RequestedThroughUtc ||
            SavedCoveragePercent != next.SavedCoveragePercent ||
            NextGapFromUtc != next.NextGapFromUtc;
        _job = next;
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return progress;
    }
}

public sealed partial class DataRetentionViewModel
{
    private static readonly TimeZoneInfo DownloadEastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
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
    private string _downloadDetail = "Save your equity list, then select Download gaps now to collect missing 15-second history.";
    private string _downloadTiming = "";

    public bool CanEditPlan => !IsBusy && !_disposed;
    public bool IsConnecting => _connecting;
    public bool IsDownloadActive => _connecting || Collector.IsBusy || _downloadCancellation is not null;
    public bool HasDownloadWork => IsDownloadActive || Jobs.Any(j =>
        j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading);
    public string DownloadState => _downloadState;
    public string DownloadHeading => _downloadHeading;
    public string DownloadDetail => _downloadDetail;
    public string DownloadTiming => _downloadTiming;
    public int DownloadTotal => Jobs.Count;
    public int DownloadProcessed => Jobs.Count(j => j.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading));
    public double DownloadProgressPercent => DownloadTotal == 0 ? 0 : Math.Clamp(Jobs.Sum(job =>
        job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading
            ? job.CheckedProgressPercent : 100d) / DownloadTotal, 0d, 100d);
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
                time != saved.DailyDownloadTime || TimeZoneId != saved.TimeZoneId ||
                !string.Equals(LibraryRootPath, saved.LibraryRootPath, StringComparison.OrdinalIgnoreCase);
        }
    }
    public string ScheduleChangesText => HasScheduleChanges
        ? "UNSAVED CHANGES. Downloads still use the saved schedule and folder. Select Save schedule & folder to apply this draft."
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
            Status = $"Download error: {exception.Message}";
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

    private void ScheduleNextDownloadCheck(bool waitingForRetry = false)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        TimeSpan delay = TimeSpan.FromSeconds(30);
        if (waitingForRetry && !_downloadsPaused && _isConnected() && _collectionError is null)
        {
            CollectionState state = Collector.State;
            DateTimeOffset? retry = state.Jobs.Where(j => j.Status == CollectionJobStatus.Pending &&
                (!j.IsAutomatic || state.Settings.AutomaticDownloadsEnabled) && j.RetryAfterUtc is not null)
                .Select(j => j.RetryAfterUtc).Min();
            if (retry is { } at && at - now < delay)
                delay = at > now ? at - now : TimeSpan.FromMilliseconds(1);
        }
        _nextDownloadCheckAt = now + delay;
        _timer.Interval = delay;
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
        int partials = state.Jobs.Count(j => j.Status == CollectionJobStatus.Partial);
        int unavailable = state.Jobs.Count(j => j.Status == CollectionJobStatus.Unavailable && !j.IsAvailabilityProbe);
        int failed = state.Jobs.Count(j => j.Status == CollectionJobStatus.Failed);
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
                "CheckingSchedule" => ("Checking saved coverage…", "Finding today's completed candles and earlier missing history for the saved equity lists."),
                "CheckingLocalHistory" => (activity.Symbol is null ? "Checking local library…" : $"Checking {target}",
                    "Reading saved candles before requesting missing 15-second data."),
                "CheckingBrokerAvailability" => ($"Checking availability · {target}",
                    "Checking missing hours for genuine 15-second candles before queuing the rest of this date."),
                "WaitingForRateLimit" => ($"Waiting briefly · {target}", "Spacing requests to the broker. Downloads will continue automatically."),
                "Saving" => ($"Saving {target}", "Validating and writing the returned candles to the local library."),
                _ => ($"Downloading {target}", "Waiting for Robinhood to return 15-second candles. This request is still active."),
            };
            if (activity.FromUtc is { } from && activity.ThroughUtc is { } through)
            {
                DateTimeOffset localFrom = TimeZoneInfo.ConvertTime(from, DownloadEastern);
                DateTimeOffset localThrough = TimeZoneInfo.ConvertTime(through, DownloadEastern);
                string end = localFrom.Date == localThrough.Date ? $"{localThrough:HH:mm:ss}" : $"{localThrough:MM-dd HH:mm:ss}";
                DownloadJobViewModel? row = Jobs.FirstOrDefault(job => job.Symbol == activity.Symbol &&
                    job.SessionDate == activity.SessionDate && job.Status == CollectionJobStatus.Downloading);
                detail = $"Request {localFrom:HH:mm:ss}–{end} Eastern. " + (activity.Stage == "CheckingBrokerAvailability"
                    ? "Checking this missing hour for available 15-second candles." : row?.CheckedProgressText ?? detail);
            }
        }
        else if (_collectionError is not null)
        {
            status = "Attention"; heading = "Download error"; detail = _collectionError;
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
                heading = retryWaiting ? "Waiting to retry" : "Continuing queued downloads";
                detail = retryWaiting
                    ? $"The broker request will be retried after {eligible.Min(j => j.RetryAfterUtc)!.Value.ToLocalTime():HH:mm:ss}. Queued work is kept."
                    : $"{eligible.Length} date/equity downloads remain. Requests run one at a time with brief pacing between them.";
            }
        }
        else if (pending.Length > 0)
        {
            status = "Paused"; heading = "Automatic queue paused";
            detail = $"{pending.Length} automatic downloads are queued. Enable and save the automatic schedule, or use Download gaps now for a manual request.";
        }
        else if (failed > 0)
        {
            status = "Attention"; heading = "Queue finished with failed downloads";
            detail = $"{failed} stock-day requests failed. See Details in the table for the errors. Previously saved candles are kept. Download gaps now checks remaining attempts; Forced download can recheck older gaps already attempted twice.";
        }
        else if (partials > 0 || unavailable > 0)
        {
            status = "Complete";
            heading = partials > 0 ? "Queue finished with partial data" : "Available-history check complete";
            detail = $"{partials} partials: stock-days with saved candles and remaining gaps. " +
                (unavailable > 0 ? $"{unavailable} stock-days have no available data. " : "") +
                "Saved candles are kept. No action is needed for ranges the broker cannot supply. Older gaps are skipped after two attempts; today's unavailable ranges can be retried after 15 minutes.";
        }
        else if (state.Jobs.Count > 0)
        {
            status = "Complete";
            bool availabilityChecked = state.Jobs.Any(j => j.IsAvailabilityProbe);
            heading = availabilityChecked ? "Available-history check complete" : "All queued downloads complete";
            detail = availabilityChecked
                ? "The availability check finished. Dates were checked from newest to oldest; each ticker stops after an older trading day is wholly unavailable after two attempts. Download again to collect newer completed candles."
                : "Completed history is saved in the local library. You can replay it or close this window.";
        }
        else
        {
            status = "Idle"; heading = "Ready to download";
            detail = "Save your equity list, then select Download gaps now. The app finds missing 15-second history and skips coverage already saved.";
        }
        string timing = _lastDownloadProgressAt is { } last
            ? (now - last < TimeSpan.FromSeconds(1) ? $"Last progress just now ({last.ToLocalTime():HH:mm:ss})."
                : $"Last progress {Elapsed(now - last)} ago ({last.ToLocalTime():HH:mm:ss}).")
            : "No collection progress since this window's data was loaded.";
        if (activity is not null) timing = (now - activity.SinceUtc < TimeSpan.FromSeconds(1)
            ? "Current step just started. " : $"Current step: {Elapsed(now - activity.SinceUtc)}. ") + timing;
        else if (!IsDownloadActive && !_downloadsPaused && eligible.Length > 0 && _started && _nextDownloadCheckAt is { } next)
            timing += $" Next queue check in {Math.Max(0, (int)Math.Ceiling((next - now).TotalSeconds))}s.";
        SetProgressText(ref _downloadState, status, nameof(DownloadState));
        SetProgressText(ref _downloadHeading, heading, nameof(DownloadHeading));
        SetProgressText(ref _downloadDetail, detail, nameof(DownloadDetail));
        SetProgressText(ref _downloadTiming, timing, nameof(DownloadTiming));
        Changed(nameof(IsConnecting)); Changed(nameof(IsDownloadActive)); Changed(nameof(HasDownloadWork)); Changed(nameof(DownloadTotal)); Changed(nameof(DownloadProcessed));
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
