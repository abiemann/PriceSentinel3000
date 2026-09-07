using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.MarketDataLibrary;

/// <summary>Durable, serial collection work. The host owns polling, connection state and cancellation.</summary>
public sealed class MarketDataCollector
{
    private readonly ICollectionStateStore _store;
    private readonly IMarketHistoryProvider _provider;
    private readonly Func<string, IMarketDataLibrary> _libraryFactory;
    private readonly TimeProvider _clock;
    private readonly CollectionRunOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CollectionState _state;
    private int _isBusy;
    private DateTimeOffset? _lastRequestAt;

    public MarketDataCollector(ICollectionStateStore store, IMarketHistoryProvider provider,
        Func<string, IMarketDataLibrary> libraryFactory, TimeProvider? clock = null, CollectionRunOptions? options = null)
    {
        _store = store;
        _provider = provider;
        _libraryFactory = libraryFactory;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new();
        if (_options.MaximumRequestsPerTick is < 1 or > 100 || _options.MaximumTransientAttempts is < 1 or > 10 ||
            _options.MinimumRequestInterval < TimeSpan.Zero || _options.RetryDelay < TimeSpan.Zero)
            throw new ArgumentException("Invalid collection request limits.");
        _state = store.Load();
        _state = _state with { Settings = _state.Settings.Validate() };
        // An interrupted process cannot leave a job permanently in Downloading.
        if (_state.Jobs.Any(j => j.Status == CollectionJobStatus.Downloading))
        {
            _state = _state with { Jobs = _state.Jobs.Select(j => j.Status == CollectionJobStatus.Downloading
                ? j with { Status = CollectionJobStatus.Pending, Error = "Interrupted download; waiting to retry.", RetryAfterUtc = null }
                : j).ToArray() };
            store.Save(_state);
        }
    }

    public CollectionState State => Snapshot(Volatile.Read(ref _state));
    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;
    public event EventHandler? StateChanged;

    public Task SaveSettingsAsync(CollectionSettings settings, CancellationToken cancellationToken = default)
    {
        CollectionSettings validated = settings.Validate();
        if (IsBusy && !SamePath(validated.LibraryRootPath, State.Settings.LibraryRootPath))
            throw new InvalidOperationException("Wait for the active download before changing the data folder.");
        return MutateAsync(() =>
        {
            CollectionSettings previous = _state.Settings;
            DateTimeOffset now = _clock.GetUtcNow();
            bool enabledNow = validated.AutomaticDownloadsEnabled && !previous.AutomaticDownloadsEnabled;
            validated = validated with
            {
                AutomaticEnabledAtUtc = validated.AutomaticDownloadsEnabled
                    ? enabledNow ? now : previous.AutomaticEnabledAtUtc ?? now
                    : null,
            };
            bool scheduleChanged = previous.DailyDownloadTime != validated.DailyDownloadTime ||
                previous.TimeZoneId != validated.TimeZoneId || previous.SessionBounds != validated.SessionBounds;
            Commit(_state with
            {
                Settings = validated,
                LastScheduledOccurrenceUtc = !validated.AutomaticDownloadsEnabled ? null :
                    enabledNow ? null : scheduleChanged ? now : _state.LastScheduledOccurrenceUtc,
            });
        }, cancellationToken);
    }

    public Task QueueManualAsync(IReadOnlyList<string> symbols, DateOnly from, DateOnly through,
        string? sessionBounds = null, CancellationToken cancellationToken = default)
    {
        string[] normalized = symbols.Select(CollectionSettings.NormalizeSymbol).Distinct().ToArray();
        if (normalized.Length is < 1 or > 1000 || through < from || through.DayNumber - from.DayNumber > 365)
            throw new ArgumentException("Select 1–1,000 equities and a date range no longer than one year.");
        return MutateAsync(() =>
        {
            string bounds = sessionBounds ?? _state.Settings.SessionBounds;
            DateOnly latest = CollectionSchedule.LatestFinalizedSession(_clock.GetUtcNow(), bounds,
                _state.Settings.ProviderFinalizationDelayMinutes);
            for (DateOnly day = latest.AddDays(1); day <= through; day = day.AddDays(1))
                if (UsEquityTradingCalendar.IsTradingDay(day))
                    throw new ArgumentException("The selected range includes a session that has not finalized yet.");
            var members = normalized.Select(symbol => _state.Settings.Lists.SelectMany(l => l.Members)
                .FirstOrDefault(m => m.Symbol == symbol && m.ProviderInstrumentId is not null) ?? new DownloadListMember(symbol)).ToArray();
            var jobs = _state.Jobs.ToList();
            for (DateOnly day = from; day <= through; day = day.AddDays(1))
                if (UsEquityTradingCalendar.IsTradingDay(day)) AddJobs(jobs, members, day, bounds, automatic: false);
            Commit(_state with { Jobs = jobs.ToArray() });
        }, cancellationToken);
    }

    public Task RetryMissingAsync(IReadOnlyCollection<Guid>? jobIds = null, CancellationToken cancellationToken = default) =>
        MutateAsync(() => Commit(_state with
        {
            Jobs = _state.Jobs.Select(j => (jobIds is null || jobIds.Contains(j.Id)) &&
                j.Status is CollectionJobStatus.Partial or CollectionJobStatus.Unavailable or CollectionJobStatus.Failed
                ? j with { Status = CollectionJobStatus.Pending, Attempts = 0, NextSourceIntervalSeconds = 15,
                    IsAutomatic = false, RetryAfterUtc = null, Error = null }
                : j).ToArray(),
        }), cancellationToken);

    public Task TickAsync(bool isConnected, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        Interlocked.Exchange(ref _isBusy, 1);
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            QueueScheduled();
            if (!isConnected) return;
            int remaining = _options.MaximumRequestsPerTick;
            foreach (CollectionJob job in _state.Jobs.Where(j => j.Status == CollectionJobStatus.Pending &&
                (!j.IsAutomatic || _state.Settings.AutomaticDownloadsEnabled) &&
                (j.RetryAfterUtc is null || j.RetryAfterUtc <= _clock.GetUtcNow())).OrderBy(j => j.QueuedAtUtc).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remaining == 0) break;
                var result = await CollectAsync(job, remaining, cancellationToken).ConfigureAwait(false);
                remaining -= result.Requests;
                if (result.ConnectionLost) break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isBusy, 0);
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }, cancellationToken);

    private void QueueScheduled()
    {
        DateTimeOffset oldest = _clock.GetUtcNow().AddDays(-_state.Settings.CatchUpCalendarDays);
        if (_state.Jobs.Any(j => j.IsAutomatic && j.Status == CollectionJobStatus.Pending &&
            CollectionSchedule.GetSessionWindow(j.SessionDate, j.SessionBounds).ThroughUtc < oldest))
            Commit(_state with { Jobs = _state.Jobs.Select(j => j.IsAutomatic && j.Status == CollectionJobStatus.Pending &&
                CollectionSchedule.GetSessionWindow(j.SessionDate, j.SessionBounds).ThroughUtc < oldest
                ? j with { Status = CollectionJobStatus.Unavailable, Error = "Missed the configured automatic catch-up window. Retry manually to check remaining provider history." }
                : j).ToArray() });
        IReadOnlyList<DueCollectionSession> due = CollectionSchedule.GetDueSessions(
            _state.Settings, _state.LastScheduledOccurrenceUtc, _clock.GetUtcNow());
        if (due.Count == 0) return;
        DownloadListMember[] members = _state.Settings.Lists.Where(l => l.IsEnabled).SelectMany(l => l.Members)
            .Where(m => m.IsIncluded).GroupBy(m => m.Symbol, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(m => m.ProviderInstrumentId is not null) ?? g.First()).ToArray();
        var jobs = _state.Jobs.ToList();
        foreach (DueCollectionSession item in due)
            AddJobs(jobs, members, item.SessionDate, _state.Settings.SessionBounds, automatic: true);
        Commit(_state with { Jobs = jobs.ToArray(), LastScheduledOccurrenceUtc = due[^1].OccurrenceUtc });
    }

    private void AddJobs(List<CollectionJob> jobs, IReadOnlyList<DownloadListMember> members, DateOnly day,
        string bounds, bool automatic)
    {
        foreach (DownloadListMember member in members)
        {
            var next = new CollectionJob
            {
                Symbol = member.Symbol, ProviderInstrumentId = member.ProviderInstrumentId, SessionDate = day,
                SessionBounds = bounds, LibraryRootPath = _state.Settings.LibraryRootPath,
                QueuedAtUtc = _clock.GetUtcNow(), IsAutomatic = automatic,
            };
            int existing = jobs.FindIndex(j => SameWork(j, next));
            if (existing < 0)
            {
                if (jobs.Count(j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading) >= 10_000)
                    throw new InvalidOperationException("The pending queue is limited to 10,000 downloads. Complete queued work before adding another range.");
                jobs.Add(next);
            }
            else if (!automatic && jobs[existing].Status != CollectionJobStatus.Complete)
                jobs[existing] = jobs[existing] with { IsAutomatic = false, Status = CollectionJobStatus.Pending,
                    Attempts = 0, NextSourceIntervalSeconds = 15, RetryAfterUtc = null, Error = null };
        }
    }

    private async Task<(int Requests, bool ConnectionLost)> CollectAsync(CollectionJob job, int requestBudget, CancellationToken cancellationToken)
    {
        int requests = 0;
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(job.SessionDate, job.SessionBounds);
        CollectionJob active = job;
        try
        {
            IMarketDataLibrary library = _libraryFactory(job.LibraryRootPath);
            HistoricalDataQueryResult saved = library.Query(new(job.Symbol, window.FromUtc, window.ThroughUtc,
                job.SourceIntervalSeconds, AdjustmentPolicy: job.AdjustmentPolicy, AdjustmentBasis: job.AdjustmentBasis,
                SessionBounds: job.SessionBounds, RevisionPolicy: HistoricalRevisionPolicy.LatestFetched));
            if (saved.Succeeded && saved.Coverage.Complete && (job.ProviderInstrumentId is null ||
                saved.Datasets.All(d => d.InstrumentId == job.ProviderInstrumentId)))
            {
                Update(job with { Status = CollectionJobStatus.Complete, ActualSourceIntervalSeconds = job.SourceIntervalSeconds,
                    DatasetHashes = saved.Datasets.Select(d => d.DatasetHash).ToArray(), Error = null });
                return (0, false);
            }
            active = job with { Status = CollectionJobStatus.Downloading, Attempts = job.Attempts + 1,
                LastAttemptAtUtc = _clock.GetUtcNow(), RetryAfterUtc = null, Error = null };
            Update(active);
            HistoricalDownload? downloaded = null;
            foreach (int interval in new[] { 15, 30, 60 }.Where(i => i >= job.NextSourceIntervalSeconds))
            {
                if (requests >= requestBudget)
                {
                    Update(active with { Status = CollectionJobStatus.Pending, Error = "Waiting for the next request allowance." });
                    return (requests, false);
                }
                if (_lastRequestAt is { } last)
                {
                    TimeSpan delay = last + _options.MinimumRequestInterval - _clock.GetUtcNow();
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
                }
                _lastRequestAt = _clock.GetUtcNow();
                requests++;
                active = active with { NextSourceIntervalSeconds = interval };
                downloaded = await _provider.DownloadHistoryAsync(new(job.Symbol, window.FromUtc, window.ThroughUtc,
                    interval, job.SessionBounds, job.AdjustmentPolicy, job.ProviderInstrumentId), cancellationToken).ConfigureAwait(false);
                if (downloaded.Symbol != job.Symbol || downloaded.SourceIntervalSeconds != interval ||
                    downloaded.SessionBounds != job.SessionBounds || downloaded.AdjustmentPolicy != job.AdjustmentPolicy ||
                    downloaded.RequestedFromUtc != window.FromUtc || downloaded.RequestedThroughUtc != window.ThroughUtc)
                    throw new InvalidDataException("The provider returned mismatched history provenance.");
                if (downloaded.Candles.Count > 0) break;
                active = active with { NextSourceIntervalSeconds = interval == 15 ? 30 : 60 };
            }
            if (downloaded is null || downloaded.Candles.Count == 0)
            {
                Update(active with { Status = CollectionJobStatus.Unavailable, Error = "No genuine 15-second, 30-second, or one-minute history is available for this session." });
                return (requests, false);
            }
            IReadOnlyList<HistoricalDatasetInfo> datasets = library.Save(downloaded);
            bool complete = downloaded.SourceIntervalSeconds == job.SourceIntervalSeconds && datasets.Count > 0 &&
                datasets.All(d => d.Coverage.Complete) && datasets.Min(d => d.Coverage.RequestedFromUtc) <= window.FromUtc &&
                datasets.Max(d => d.Coverage.RequestedThroughUtc) >= window.ThroughUtc;
            Update(active with
            {
                Status = complete ? CollectionJobStatus.Complete : CollectionJobStatus.Partial,
                ActualSourceIntervalSeconds = downloaded.SourceIntervalSeconds,
                AdjustmentBasis = downloaded.AdjustmentBasis,
                DatasetHashes = datasets.Select(d => d.DatasetHash).ToArray(),
                Error = complete ? null : downloaded.SourceIntervalSeconds != job.SourceIntervalSeconds
                    ? $"Saved genuine {downloaded.SourceIntervalSeconds}-second history; 15-second coverage is unavailable."
                    : "Saved available history; coverage has gaps.",
            });
        }
        catch (MarketDataConnectionUnavailableException exception)
        {
            Update(active with { Status = CollectionJobStatus.Pending, Attempts = job.Attempts,
                Error = exception.Message, RetryAfterUtc = null });
            return (requests, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Update(active with { Status = CollectionJobStatus.Pending, Attempts = job.Attempts,
                Error = "Download interrupted; waiting for the connection and a retry.", RetryAfterUtc = null });
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            bool retry = active.Attempts < _options.MaximumTransientAttempts;
            Update(active with { Status = retry ? CollectionJobStatus.Pending : CollectionJobStatus.Failed,
                Error = exception.Message, RetryAfterUtc = retry ? _clock.GetUtcNow() + _options.RetryDelay : null });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Update(active with { Status = CollectionJobStatus.Failed, Error = exception.Message, RetryAfterUtc = null });
        }
        return (requests, false);
    }

    private Task MutateAsync(Action mutation, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { mutation(); }
        finally { _gate.Release(); }
    }, cancellationToken);

    private void Update(CollectionJob job) => Commit(_state with
    {
        Jobs = _state.Jobs.Select(j => j.Id == job.Id ? job : j).ToArray(),
    });

    private void Commit(CollectionState state)
    {
        // Keep a bounded operational history; immutable candle files remain in the library.
        if (state.Jobs.Count(j => j.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading)) > 10_000)
        {
            HashSet<Guid> retain = state.Jobs.Where(j => j.Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading))
                .OrderByDescending(j => j.LastAttemptAtUtc ?? j.QueuedAtUtc).Take(10_000).Select(j => j.Id).ToHashSet();
            state = state with { Jobs = state.Jobs.Where(j => j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading || retain.Contains(j.Id)).ToArray() };
        }
        _store.Save(state);
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool SameWork(CollectionJob left, CollectionJob right) =>
        (left.Symbol == right.Symbol || (left.ProviderInstrumentId is not null && left.ProviderInstrumentId == right.ProviderInstrumentId)) &&
        left.SessionDate == right.SessionDate && left.SourceIntervalSeconds == right.SourceIntervalSeconds &&
        left.SessionBounds == right.SessionBounds && left.AdjustmentPolicy == right.AdjustmentPolicy &&
        left.AdjustmentBasis == right.AdjustmentBasis && SamePath(left.LibraryRootPath, right.LibraryRootPath);

    private static CollectionState Snapshot(CollectionState state) => state with
    {
        Settings = state.Settings with { Lists = state.Settings.Lists.Select(l => l with { Members = l.Members.ToArray() }).ToArray() },
        Jobs = state.Jobs.Select(j => j with { DatasetHashes = j.DatasetHashes.ToArray() }).ToArray(),
    };
}
