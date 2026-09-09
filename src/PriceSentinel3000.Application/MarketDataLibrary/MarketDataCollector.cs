using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.MarketDataLibrary;

/// <summary>Durable, serial collection work. The host owns polling, connection state and cancellation.</summary>
public sealed partial class MarketDataCollector
{
    private readonly ICollectionStateStore _store;
    private readonly IMarketHistoryProvider _provider;
    private readonly Func<string, IMarketDataLibrary> _libraryFactory;
    private readonly Func<string, ICollectionGapIndex>? _gapIndexFactory;
    private readonly TimeProvider _clock;
    private readonly CollectionRunOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CollectionState _state;
    private int _isBusy;
    private CollectionActivity? _activity;
    private DateTimeOffset? _lastRequestAt;

    public MarketDataCollector(ICollectionStateStore store, IMarketHistoryProvider provider,
        Func<string, IMarketDataLibrary> libraryFactory, TimeProvider? clock = null, CollectionRunOptions? options = null,
        Func<string, ICollectionGapIndex>? gapIndexFactory = null)
    {
        _store = store;
        _provider = provider;
        _libraryFactory = libraryFactory;
        _gapIndexFactory = gapIndexFactory;
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
    public CollectionActivity? Activity => Volatile.Read(ref _activity);
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
                if (CollectionSchedule.IsCollectionDate(day, bounds))
                    throw new ArgumentException("The selected range includes a session that has not finalized yet.");
            var members = normalized.Select(symbol => _state.Settings.Lists.SelectMany(l => l.Members)
                .FirstOrDefault(m => m.Symbol == symbol && m.ProviderInstrumentId is not null) ?? new DownloadListMember(symbol)).ToArray();
            var jobs = _state.Jobs.ToList();
            for (DateOnly day = from; day <= through; day = day.AddDays(1))
                if (CollectionSchedule.IsCollectionDate(day, bounds)) AddJobs(jobs, members, day, bounds, automatic: false);
            Commit(_state with { Jobs = jobs.ToArray() });
        }, cancellationToken);
    }

    public Task RetryMissingAsync(IReadOnlyCollection<Guid>? jobIds = null, CancellationToken cancellationToken = default) =>
        MutateAsync(() => Commit(_state with
        {
            Jobs = _state.Jobs.Select(j => (jobIds is null || jobIds.Contains(j.Id)) &&
                j.Status is CollectionJobStatus.Partial or CollectionJobStatus.Unavailable or CollectionJobStatus.Failed
                ? j with { Status = CollectionJobStatus.Pending, Attempts = 0, NextSourceIntervalSeconds = 15,
                    IsAutomatic = false, IsAvailabilityProbe = false, IgnoreKnownGaps = false, RetryAfterUtc = null, Error = null, NextGapFromUtc = null, ReceivedCandlesThisRun = false,
                    DiscoveryAsOfDate = null, AvailabilityCheckPending = false, DiscoveryEmptySessions = null }
                : j).ToArray(),
        }), cancellationToken);

    /// <returns>Whether the host can continue immediately, wait for a retry, or await a connection.</returns>
    public Task<CollectionBatchResult> TickAsync(bool isConnected, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return CollectionBatchResult.Busy;
        Interlocked.Exchange(ref _isBusy, 1);
        try
        {
            SetActivity("CheckingSchedule");
            QueueScheduled();
            if (!isConnected) return CollectionBatchResult.Disconnected;
            int remaining = _options.MaximumRequestsPerTick;
            foreach (CollectionJob job in _state.Jobs.Where(j => j.Status == CollectionJobStatus.Pending &&
                (!j.IsAutomatic || _state.Settings.AutomaticDownloadsEnabled) &&
                (j.RetryAfterUtc is null || j.RetryAfterUtc <= _clock.GetUtcNow())).OrderByDescending(j => j.SessionDate).ThenBy(j => j.QueuedAtUtc).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remaining == 0) break;
                CollectionJob current = _state.Jobs.First(j => j.Id == job.Id);
                if (current.Status != CollectionJobStatus.Pending) continue;
                var result = await CollectAsync(current, remaining, cancellationToken).ConfigureAwait(false);
                remaining -= result.Requests;
                if (result.ConnectionLost) return CollectionBatchResult.Disconnected;
            }
            CollectionJob[] pending = _state.Jobs.Where(j => j.Status == CollectionJobStatus.Pending &&
                (!j.IsAutomatic || _state.Settings.AutomaticDownloadsEnabled)).ToArray();
            return pending.Any(j => j.RetryAfterUtc is null || j.RetryAfterUtc <= _clock.GetUtcNow())
                ? CollectionBatchResult.Ready : pending.Length > 0
                    ? CollectionBatchResult.WaitingForRetry : CollectionBatchResult.Idle;
        }
        finally
        {
            Volatile.Write(ref _activity, null);
            Interlocked.Exchange(ref _isBusy, 0);
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }, cancellationToken);

    private void QueueScheduled()
    {
        // A due schedule must not replace an explicit forced run or reset its retry
        // progress. Leave the occurrence due until that run has finished.
        if (_state.Jobs.Any(j => j.IgnoreKnownGaps &&
            j.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading &&
            SamePath(j.LibraryRootPath, _state.Settings.LibraryRootPath))) return;
        const string bounds = CollectionSettings.AllAvailableSessionBounds;
        IReadOnlyList<DueCollectionSession> due = CollectionSchedule.GetDueSessions(
            _state.Settings with { SessionBounds = bounds }, _state.LastScheduledOccurrenceUtc, _clock.GetUtcNow());
        if (due.Count == 0) return;
        DownloadListMember[] members = _state.Settings.Lists.Where(l => l.IsEnabled).SelectMany(l => l.Members)
            .Where(m => m.IsIncluded).GroupBy(m => m.Symbol, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(m => m.ProviderInstrumentId is not null) ?? g.First()).ToArray();
        SetActivity("CheckingLocalHistory");
        MarketDataLibraryScan scan = _libraryFactory(_state.Settings.LibraryRootPath).Scan();
        if (scan.Diagnostics.Any(d => d.Code is "scan_limit" or "scan_failed"))
            throw new InvalidDataException("The library must scan completely before checking download continuity.");
        var gaps = _state.ContinuityGaps.Where(g => !SamePath(g.LibraryRootPath, _state.Settings.LibraryRootPath) ||
            g.SessionBounds != bounds || !members.Any(m => m.Symbol == g.Symbol)).ToList();
        foreach (DownloadListMember member in members)
        {
            DateOnly? trackedFrom = _state.Jobs.Where(j => j.Symbol == member.Symbol &&
                (!j.IsAvailabilityProbe || j.DatasetHashes.Count > 0 || j.Status == CollectionJobStatus.Failed) &&
                j.SessionBounds == bounds && SamePath(j.LibraryRootPath, _state.Settings.LibraryRootPath))
                .Select(j => (DateOnly?)j.SessionDate)
                .Concat(_state.ContinuityGaps.Where(g => g.Symbol == member.Symbol &&
                    g.SessionBounds == bounds && SamePath(g.LibraryRootPath, _state.Settings.LibraryRootPath))
                    .Select(g => (DateOnly?)g.FromSessionDate)).Min();
            CollectionBackfillPlan plan = CollectionBackfillPlanner.Plan(member, scan.Datasets, due[^1].SessionDate,
                bounds, _clock.GetUtcNow(), _state.Settings.CatchUpCalendarDays, trackedFrom);
            gaps.AddRange(plan.ExpiredGaps.Select(g => new CollectionContinuityGap(member.Symbol,
                g.FromSessionDate, g.ThroughSessionDate, bounds, _state.Settings.LibraryRootPath)));
        }
        Commit(_state with { ContinuityGaps = gaps.ToArray() });
        QueueAvailable(automatic: true);
        Commit(_state with { LastScheduledOccurrenceUtc = due[^1].OccurrenceUtc });
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
            else if (!automatic ||
                (automatic && jobs[existing].Status is not (CollectionJobStatus.Pending or CollectionJobStatus.Downloading)))
                jobs[existing] = jobs[existing] with { IsAutomatic = automatic && jobs[existing].IsAutomatic, Status = CollectionJobStatus.Pending,
                    Attempts = 0, NextSourceIntervalSeconds = 15, RetryAfterUtc = null, Error = null,
                    RequestedThroughUtc = null, NextGapFromUtc = null, ReceivedCandlesThisRun = false,
                    DiscoveryAsOfDate = null, AvailabilityCheckPending = false, IgnoreKnownGaps = false,
                    DiscoveryEmptySessions = automatic ? jobs[existing].DiscoveryEmptySessions : null,
                    IsAvailabilityProbe = automatic && jobs[existing].IsAvailabilityProbe };
        }
    }

    private async Task<(int Requests, bool ConnectionLost)> CollectAsync(CollectionJob job, int requestBudget, CancellationToken cancellationToken)
    {
        if (job.SessionBounds == CollectionSettings.AllAvailableSessionBounds)
            return await CollectAllHoursAsync(job, requestBudget, cancellationToken).ConfigureAwait(false);
        int requests = 0;
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(job.SessionDate, job.SessionBounds);
        if (job.RequestedThroughUtc is { } cap)
        {
            if (cap <= window.FromUtc || cap > window.ThroughUtc || cap.UtcTicks % (15 * TimeSpan.TicksPerSecond) != 0)
                throw new InvalidDataException("The requested collection cutoff is outside the market session.");
            window = window with { ThroughUtc = cap };
        }
        CollectionJob active = job;
        try
        {
            SetActivity("CheckingLocalHistory", job);
            IMarketDataLibrary library = _libraryFactory(job.LibraryRootPath);
            HistoricalDataQueryResult saved = library.Query(new(job.Symbol, window.FromUtc, window.ThroughUtc,
                15, AdjustmentPolicy: job.AdjustmentPolicy, AdjustmentBasis: job.AdjustmentBasis,
                SessionBounds: job.SessionBounds, RevisionPolicy: HistoricalRevisionPolicy.LatestFetched,
                IncludeCompatibleSessions: true));
            if (!saved.Succeeded)
                throw new InvalidDataException("Saved history could not be validated for gap recovery. Review the local library diagnostics.");
            if (job.ProviderInstrumentId is not null && saved.Datasets.Any(d => d.InstrumentId != job.ProviderInstrumentId))
                throw new InvalidDataException("Saved history belongs to a different instrument; it cannot be used for gap recovery.");
            if (saved.Succeeded && saved.Coverage.Complete && (job.ProviderInstrumentId is null ||
                saved.Datasets.All(d => d.InstrumentId == job.ProviderInstrumentId)))
            {
                FinishCollection(job with { Status = CollectionJobStatus.Complete, ActualSourceIntervalSeconds = 15,
                    DatasetHashes = saved.Datasets.Select(d => d.DatasetHash).ToArray(), Error = null });
                return (0, false);
            }
            active = job with { Status = CollectionJobStatus.Downloading, Attempts = job.Attempts + 1,
                LastAttemptAtUtc = _clock.GetUtcNow(), NextSourceIntervalSeconds = 15, RetryAfterUtc = null, Error = null };
            Update(active);
            if (requestBudget < 1)
            {
                Update(active with { Status = CollectionJobStatus.Pending, Error = "Waiting for the next request allowance." });
                return (requests, false);
            }
            if (_lastRequestAt is { } last)
            {
                TimeSpan delay = last + _options.MinimumRequestInterval - _clock.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    SetActivity("WaitingForRateLimit", job);
                    await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
                }
            }
            // Start at the earliest hole, not merely after the newest stored candle: an earlier
            // outage still needs repair when later candles/days have already been downloaded.
            DateTimeOffset from = saved.Coverage.Gaps.Count > 0 ? saved.Coverage.Gaps.Min(g => g.FromUtc) : window.FromUtc;
            _lastRequestAt = _clock.GetUtcNow();
            requests++;
            SetActivity("Downloading", job);
            HistoricalDownload downloaded = await _provider.DownloadHistoryAsync(new(job.Symbol, from, window.ThroughUtc,
                15, job.SessionBounds, job.AdjustmentPolicy, job.ProviderInstrumentId), cancellationToken).ConfigureAwait(false);
            if (downloaded.Symbol != job.Symbol || downloaded.SourceIntervalSeconds != 15 ||
                downloaded.SessionBounds != job.SessionBounds || downloaded.AdjustmentPolicy != job.AdjustmentPolicy ||
                downloaded.AdjustmentBasis != job.AdjustmentBasis ||
                (job.ProviderInstrumentId is not null && downloaded.InstrumentId != job.ProviderInstrumentId) ||
                downloaded.RequestedFromUtc != from || downloaded.RequestedThroughUtc != window.ThroughUtc ||
                downloaded.Candles.Any(c => c.StartsAtUtc < from || c.EndsAtUtc > window.ThroughUtc))
                throw new InvalidDataException("The provider returned mismatched 15-second history provenance.");
            if (downloaded.Candles.Count == 0)
            {
                FinishCollection(active with { Status = saved.Candles.Count > 0 ? CollectionJobStatus.Partial : CollectionJobStatus.Unavailable,
                    ActualSourceIntervalSeconds = saved.Candles.Count > 0 ? 15 : null,
                    DatasetHashes = saved.Datasets.Select(d => d.DatasetHash).ToArray(),
                    Error = "No 15-second candles were returned for the missing range. Provider retention may have expired; the gap remains unresolved. Coarser data is not substituted." },
                    emptySession: saved.Candles.Count == 0);
                return (requests, false);
            }
            if (saved.Datasets.Any(d => d.Provider != downloaded.Provider || d.InstrumentId != downloaded.InstrumentId ||
                d.AdjustmentPolicy != downloaded.AdjustmentPolicy || d.AdjustmentBasis != downloaded.AdjustmentBasis))
                throw new InvalidDataException("Downloaded and saved history have different provenance; they cannot be merged.");
            var candles = saved.Candles.ToDictionary(c => c.StartsAtUtc);
            var received = new HashSet<DateTimeOffset>();
            foreach (HistoricalCandle candle in downloaded.Candles)
            {
                if (!received.Add(candle.StartsAtUtc))
                    throw new InvalidDataException("The provider returned duplicate candle timestamps.");
                if (candles.TryGetValue(candle.StartsAtUtc, out HistoricalCandle? previous) && previous != candle)
                    throw new InvalidDataException("Provider revisions changed overlapping saved candles. Gap recovery cannot blend different revisions; existing files were preserved.");
                candles[candle.StartsAtUtc] = candle;
            }
            downloaded = downloaded with { RequestedFromUtc = window.FromUtc,
                Candles = candles.Values.OrderBy(c => c.StartsAtUtc).ToArray() };
            SetActivity("Saving", job);
            IReadOnlyList<HistoricalDatasetInfo> datasets = library.Save(downloaded);
            bool complete = datasets.Count > 0 &&
                datasets.All(d => d.Coverage.Complete) && datasets.Min(d => d.Coverage.RequestedFromUtc) <= window.FromUtc &&
                datasets.Max(d => d.Coverage.RequestedThroughUtc) >= window.ThroughUtc;
            FinishCollection(active with
            {
                Status = complete ? CollectionJobStatus.Complete : CollectionJobStatus.Partial,
                ActualSourceIntervalSeconds = downloaded.SourceIntervalSeconds,
                AdjustmentBasis = downloaded.AdjustmentBasis,
                DatasetHashes = datasets.Select(d => d.DatasetHash).ToArray(),
                Error = complete ? null : "Saved genuine 15-second history; coverage has gaps. Recent gaps are checked again on the next scheduled run.",
            }, receivedCandles: true);
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

    private void SetActivity(string stage, CollectionJob? job = null,
        DateTimeOffset? fromUtc = null, DateTimeOffset? throughUtc = null)
    {
        Volatile.Write(ref _activity, new(stage, job?.Symbol, job?.SessionDate, _clock.GetUtcNow(), fromUtc, throughUtc));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Update(CollectionJob job) => Commit(_state with
    {
        Jobs = _state.Jobs.Select(j => j.Id == job.Id ? job : j).ToArray(),
        ContinuityGaps = job.Status == CollectionJobStatus.Complete
            ? _state.ContinuityGaps.SelectMany(g => RemoveRepairedSession(g, job)).ToArray() : _state.ContinuityGaps,
    });

    private static IEnumerable<CollectionContinuityGap> RemoveRepairedSession(CollectionContinuityGap gap, CollectionJob job)
    {
        if (gap.Symbol != job.Symbol || (gap.SessionBounds != job.SessionBounds && !(job.SessionBounds == "24_5" && gap.SessionBounds is "regular" or "extended")) || !SamePath(gap.LibraryRootPath, job.LibraryRootPath) ||
            job.SessionDate < gap.FromSessionDate || job.SessionDate > gap.ThroughSessionDate)
        {
            yield return gap;
            yield break;
        }
        DateOnly before = job.SessionDate.AddDays(-1), after = job.SessionDate.AddDays(1);
        while (before >= gap.FromSessionDate && !CollectionSchedule.IsCollectionDate(before, gap.SessionBounds)) before = before.AddDays(-1);
        while (after <= gap.ThroughSessionDate && !CollectionSchedule.IsCollectionDate(after, gap.SessionBounds)) after = after.AddDays(1);
        if (before >= gap.FromSessionDate) yield return gap with { ThroughSessionDate = before };
        if (after <= gap.ThroughSessionDate) yield return gap with { FromSessionDate = after };
    }

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
        (left.ProviderInstrumentId is null || right.ProviderInstrumentId is null ||
            left.ProviderInstrumentId == right.ProviderInstrumentId) &&
        (left.Symbol == right.Symbol || (left.ProviderInstrumentId is not null && left.ProviderInstrumentId == right.ProviderInstrumentId)) &&
        left.SessionDate == right.SessionDate && left.SourceIntervalSeconds == right.SourceIntervalSeconds &&
        left.SessionBounds == right.SessionBounds && left.AdjustmentPolicy == right.AdjustmentPolicy &&
        left.AdjustmentBasis == right.AdjustmentBasis && SamePath(left.LibraryRootPath, right.LibraryRootPath);

    private static CollectionState Snapshot(CollectionState state) => state with
    {
        Settings = state.Settings with { Lists = state.Settings.Lists.Select(l => l with { Members = l.Members.ToArray() }).ToArray() },
        Jobs = state.Jobs.Select(j => j with { DatasetHashes = j.DatasetHashes.ToArray() }).ToArray(),
        ContinuityGaps = state.ContinuityGaps.ToArray(),
    };
}
