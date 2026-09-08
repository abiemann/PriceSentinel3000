using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private static readonly TimeZoneInfo CollectionEastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const int EmptySessionsToStopDiscovery = 3;

    /// <summary>Collects missing genuine 15-second candles, discovering each equity's older availability.</summary>
    public Task QueueAvailableAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(() => QueueAvailable(automatic: false), cancellationToken);

    private void QueueAvailable(bool automatic)
    {
        DownloadListMember[] members = _state.Settings.Lists.Where(l => l.IsEnabled).SelectMany(l => l.Members)
            .Where(m => m.IsIncluded).GroupBy(m => m.Symbol, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(m => m.ProviderInstrumentId is not null) ?? g.First()).ToArray();
        if (members.Length == 0)
        {
            if (automatic) return;
            throw new ArgumentException("Save a list with at least one included equity before downloading.");
        }
        DateTimeOffset now = _clock.GetUtcNow();
        string bounds = CollectionSettings.AllAvailableSessionBounds;
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, CollectionEastern).DateTime);
        DateOnly latest = CollectionSchedule.LatestFinalizedSession(now, bounds,
            _state.Settings.ProviderFinalizationDelayMinutes);
        DateTimeOffset? partialThrough = null;
        if (CollectionSchedule.IsCollectionDate(today, bounds))
        {
            CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(today, bounds);
            DateTimeOffset completed = new(now.UtcTicks - now.UtcTicks % (15 * TimeSpan.TicksPerSecond), TimeSpan.Zero);
            if (completed > window.FromUtc && today > latest)
            {
                latest = today;
                partialThrough = completed < window.ThroughUtc ? completed : window.ThroughUtc;
            }
        }

        // The retry horizon controls the first batch only, never the provider's retention boundary.
        DateOnly first = today.AddDays(-_state.Settings.CatchUpCalendarDays + 1);
        if (first > latest) first = latest;
        while (!CollectionSchedule.IsCollectionDate(first, bounds)) first = first.AddDays(1);
        var jobs = _state.Jobs.Select(j => members.Any(m => m.Symbol == j.Symbol) &&
            j.SessionBounds == bounds && SamePath(j.LibraryRootPath, _state.Settings.LibraryRootPath)
            ? j with { DiscoveryEmptySessions = null } : j).ToList();
        for (DateOnly day = latest; day >= first; day = day.AddDays(-1))
        {
            if (!CollectionSchedule.IsCollectionDate(day, bounds)) continue;
            AddJobs(jobs, members, day, bounds, automatic);
            foreach (DownloadListMember member in members)
            {
                int index = FindAvailabilityJob(jobs, member, day, bounds, _state.Settings.LibraryRootPath);
                jobs[index] = jobs[index] with
                {
                    IsAvailabilityProbe = true,
                    RequestedThroughUtc = day == latest ? partialThrough : null,
                    DiscoveryEmptySessions = day == first ? 0 : null,
                };
            }
        }
        QueueKnownMissing(jobs, members, first, latest, bounds, now, automatic);
        Commit(_state with { Jobs = jobs.ToArray() });
    }

    private void QueueKnownMissing(List<CollectionJob> jobs, DownloadListMember[] members,
        DateOnly firstDiscoveryDate, DateOnly latest, string bounds, DateTimeOffset now, bool automatic)
    {
        MarketDataLibraryScan scan = _libraryFactory(_state.Settings.LibraryRootPath).Scan();
        if (scan.Diagnostics.Any(d => d.Code is "scan_limit" or "scan_failed"))
            throw new InvalidDataException("The library must scan completely before checking missing downloads.");
        foreach (DownloadListMember member in members)
        {
            // Known failures and saved partial days still deserve a retry even when an
            // empty broker window stops ordinary discovery before reaching their dates.
            DateOnly[] retryDates = jobs.Where(j => j.Symbol == member.Symbol &&
                j.SessionDate < firstDiscoveryDate && j.SessionBounds is "regular" or "extended" or "24_5" &&
                (member.ProviderInstrumentId is null || j.ProviderInstrumentId is null ||
                    j.ProviderInstrumentId == member.ProviderInstrumentId) &&
                SamePath(j.LibraryRootPath, _state.Settings.LibraryRootPath) &&
                j.SourceIntervalSeconds == 15 && j.AdjustmentPolicy == "split" &&
                j.AdjustmentBasis == "robinhood-split-unversioned" &&
                (j.Status is CollectionJobStatus.Partial or CollectionJobStatus.Failed ||
                    j.Status == CollectionJobStatus.Unavailable && (!j.IsAvailabilityProbe || j.DatasetHashes.Count > 0)))
                .Select(j => j.SessionDate).ToArray();
            CollectionBackfillPlan plan = CollectionBackfillPlanner.Plan(member, scan.Datasets, latest,
                bounds, now, _state.Settings.CatchUpCalendarDays);
            DateOnly[] partialDates = scan.Datasets.Where(d => d.Symbol == member.Symbol &&
                d.SourceIntervalSeconds == 15 && d.SessionBounds is "regular" or "extended" or "24_5" && d.TradingDate < firstDiscoveryDate &&
                d.AdjustmentPolicy == "split" && d.AdjustmentBasis == "robinhood-split-unversioned" &&
                (member.ProviderInstrumentId is null || d.InstrumentId == member.ProviderInstrumentId) &&
                (plan.MissingSessions.Contains(d.TradingDate) ||
                    plan.ExpiredGaps.Any(g => d.TradingDate >= g.FromSessionDate && d.TradingDate <= g.ThroughSessionDate)))
                .Select(d => d.TradingDate).ToArray();
            foreach (DateOnly day in retryDates.Concat(partialDates).Where(day => CollectionSchedule.IsCollectionDate(day, bounds)).Distinct().OrderDescending())
                AddJobs(jobs, [member], day, bounds, automatic);
        }
    }

    // Advancing the frontier and finishing its job are one durable state write. A crash
    // cannot leave successful discovery permanently stopped between two sessions.
    private void FinishCollection(CollectionJob job, bool emptySession = false, bool receivedCandles = false)
    {
        if (emptySession && job.IsAvailabilityProbe)
            job = job with { Error = job.DiscoveryEmptySessions is >= EmptySessionsToStopDiscovery - 1
                ? "No genuine 15-second candles are available. Earlier discovery stopped after three consecutive collection dates with no broker data."
                : "No genuine 15-second candles are available for this date." };
        var jobs = _state.Jobs.Select(j => j.Id == job.Id ? job with { DiscoveryEmptySessions = null } : j).ToList();
        if (job.DiscoveryEmptySessions is { } precedingEmpty)
        {
            int empty = emptySession ? precedingEmpty + 1 : receivedCandles ? 0 : precedingEmpty;
            if (empty < EmptySessionsToStopDiscovery && job.SessionDate > DateOnly.MinValue)
            {
                DateOnly previous = job.SessionDate.AddDays(-1);
                while (previous > DateOnly.MinValue && !CollectionSchedule.IsCollectionDate(previous, job.SessionBounds))
                    previous = previous.AddDays(-1);
                if (CollectionSchedule.IsCollectionDate(previous, job.SessionBounds))
                {
                    var next = job with
                    {
                        Id = Guid.NewGuid(), SessionDate = previous, RequestedThroughUtc = null,
                        DiscoveryEmptySessions = empty, Status = CollectionJobStatus.Pending,
                        ActualSourceIntervalSeconds = null, DatasetHashes = [], Attempts = 0,
                        NextGapFromUtc = null, ReceivedCandlesThisRun = false,
                        QueuedAtUtc = _clock.GetUtcNow(), LastAttemptAtUtc = null, RetryAfterUtc = null, Error = null,
                    };
                    int existing = jobs.FindIndex(j => SameWork(j, next));
                    if (existing >= 0)
                    {
                        CollectionJob previousWork = jobs[existing];
                        next = previousWork.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading
                            ? previousWork with { DiscoveryEmptySessions = empty, IsAvailabilityProbe = true,
                                RequestedThroughUtc = null }
                            : next with { Id = previousWork.Id };
                    }
                    if (existing >= 0) jobs[existing] = next;
                    else jobs.Add(next);
                }
            }
        }
        Commit(_state with
        {
            Jobs = jobs.ToArray(),
            ContinuityGaps = job.Status == CollectionJobStatus.Complete && job.RequestedThroughUtc is null
                ? _state.ContinuityGaps.SelectMany(g => RemoveRepairedSession(g, job)).ToArray() : _state.ContinuityGaps,
        });
    }

    private static int FindAvailabilityJob(List<CollectionJob> jobs, DownloadListMember member, DateOnly day,
        string bounds, string root) => jobs.FindIndex(j => SameWork(j, new CollectionJob
        {
            Symbol = member.Symbol, ProviderInstrumentId = member.ProviderInstrumentId, SessionDate = day,
            SessionBounds = bounds, LibraryRootPath = root,
        }));
}
