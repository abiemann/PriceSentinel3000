using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private static readonly TimeZoneInfo CollectionEastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const int EmptySessionsToStopDiscovery = 3;

    /// <summary>Collects missing genuine 15-second candles, discovering each equity's older availability.</summary>
    public Task QueueAvailableAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(() => QueueAvailable(automatic: false), cancellationToken);

    /// <summary>Retries genuine missing candles, including ranges previously reported empty.</summary>
    public Task QueueForcedAvailableAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(() => QueueAvailable(automatic: false, ignoreKnownGaps: true), cancellationToken);

    private void QueueAvailable(bool automatic, bool ignoreKnownGaps = false)
    {
        DownloadListMember[] members = _state.Settings.Lists.Where(l => l.IsEnabled).SelectMany(l => l.Members)
            .Where(m => m.IsIncluded).GroupBy(m => m.Symbol, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(m => m.ProviderInstrumentId is not null) ?? g.First()).ToArray();
        if (members.Length == 0)
        {
            if (automatic) return;
            throw new ArgumentException("Save a list with at least one included equity before downloading.");
        }
        bool tracksAttempts = GetGapIndex(_state.Settings.LibraryRootPath)?.SupportsAttemptTracking == true;
        DateTimeOffset now = _clock.GetUtcNow();
        string bounds = CollectionSettings.AllAvailableSessionBounds;
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, CollectionEastern).DateTime);
        DateOnly latest = CollectionSchedule.LatestFinalizedSession(now, bounds, finalizationDelayMinutes: 0);
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

        // A tracked run replaces its prior availability rows. Explicit work and other
        // scopes remain; legacy indexes retain their terminal history.
        var jobs = _state.Jobs.Where(j => !((tracksAttempts || j.Status == CollectionJobStatus.Pending) && j.IsAvailabilityProbe &&
            j.SourceIntervalSeconds == 15 && j.AdjustmentPolicy == "split" &&
            j.AdjustmentBasis == "robinhood-split-unversioned" &&
            j.SessionBounds is "regular" or "extended" or "24_5" &&
            SamePath(j.LibraryRootPath, _state.Settings.LibraryRootPath) && members.Any(m => m.Symbol == j.Symbol &&
                (m.ProviderInstrumentId is null || j.ProviderInstrumentId is null ||
                    m.ProviderInstrumentId == j.ProviderInstrumentId)))).ToList();
        if (!tracksAttempts)
        {
            QueueLegacyAvailability(jobs, members, latest, today, partialThrough, bounds, automatic, ignoreKnownGaps);
            return;
        }
        var run = new CollectionAvailabilityRun
        {
            AsOfDate = today, CurrentDate = latest, LibraryRootPath = _state.Settings.LibraryRootPath,
            Members = members, IsAutomatic = automatic, IgnoreKnownGaps = ignoreKnownGaps,
        };
        var currentJobs = new List<Guid>();
        foreach (DownloadListMember member in members)
        {
            CollectionJob candidate = AvailabilityJob(run, member, latest) with { RequestedThroughUtc = partialThrough };
            currentJobs.Add(QueueAvailabilityJob(jobs, candidate));
        }
        // The newest date always appears. Older dates are checked against local
        // files and persistent attempt counts before any row is added.
        Commit(_state with { Jobs = jobs.ToArray(), AvailabilityRun = run with { CurrentJobIds = currentJobs.ToArray() } });
    }

    private void QueueLegacyAvailability(List<CollectionJob> jobs, DownloadListMember[] members, DateOnly latest,
        DateOnly today, DateTimeOffset? partialThrough, string bounds, bool automatic, bool ignoreKnownGaps)
    {
        AddJobs(jobs, members, latest, bounds, automatic);
        foreach (DownloadListMember member in members)
        {
            int index = FindAvailabilityJob(jobs, member, latest, bounds, _state.Settings.LibraryRootPath);
            if (ignoreKnownGaps)
                jobs[index] = jobs[index] with
                {
                    Status = CollectionJobStatus.Pending, Attempts = 0, RetryAfterUtc = null,
                    LastAttemptAtUtc = null, Error = null, NextSourceIntervalSeconds = 15,
                };
            jobs[index] = jobs[index] with
            {
                IsAvailabilityProbe = true, IgnoreKnownGaps = ignoreKnownGaps,
                RequestedThroughUtc = partialThrough, DiscoveryAsOfDate = today,
                AvailabilityCheckPending = latest < today, DiscoveryEmptySessions = 0,
                NextGapFromUtc = null, ReceivedCandlesThisRun = false,
            };
        }
        Commit(_state with { Jobs = jobs.ToArray() });
    }

    // Advancing the frontier and finishing its job are one durable state write. A crash
    // cannot leave successful discovery permanently stopped between two sessions.
    private void FinishCollection(CollectionJob job, bool emptySession = false, bool receivedCandles = false)
    {
        if (job.AvailabilityRunId is not null)
        {
            Commit(_state with
            {
                Jobs = _state.Jobs.Select(existing => existing.Id == job.Id
                    ? job with { DiscoveryEmptySessions = null, AvailabilityCheckPending = false } : existing).ToArray(),
                ContinuityGaps = job.Status == CollectionJobStatus.Complete && job.RequestedFromUtc is null && job.RequestedThroughUtc is null
                    ? _state.ContinuityGaps.SelectMany(gap => RemoveRepairedSession(gap, job)).ToArray() : _state.ContinuityGaps,
            });
            return;
        }
        bool progressive = job.DiscoveryAsOfDate is not null;
        bool continueDiscovery = !progressive || job.Status == CollectionJobStatus.Complete ||
            receivedCandles || job.ReceivedCandlesThisRun || job.SessionDate == job.DiscoveryAsOfDate ||
            !UsEquityTradingCalendar.IsTradingDay(job.SessionDate);
        if (emptySession && job.IsAvailabilityProbe)
            job = job with { Error = progressive
                ? continueDiscovery ? "No genuine 15-second candles were returned for this date."
                    : "No genuine 15-second candles were returned after checking this date's gaps. Earlier discovery stopped."
                : job.DiscoveryEmptySessions is >= EmptySessionsToStopDiscovery - 1
                    ? "No genuine 15-second candles are available. Earlier discovery stopped after three consecutive collection dates with no broker data."
                    : "No genuine 15-second candles are available for this date." };
        var jobs = _state.Jobs.Select(j => j.Id == job.Id
            ? job with { DiscoveryEmptySessions = null, AvailabilityCheckPending = false } : j).ToList();
        if (job.DiscoveryEmptySessions is { } precedingEmpty)
        {
            int empty = progressive ? 0 : emptySession ? precedingEmpty + 1 : receivedCandles ? 0 : precedingEmpty;
            if (continueDiscovery && empty < EmptySessionsToStopDiscovery && job.SessionDate > DateOnly.MinValue)
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
                        AvailabilityCheckPending = progressive,
                        ActualSourceIntervalSeconds = null, DatasetHashes = [], Attempts = 0, SavedCoveragePercent = null,
                        NextGapFromUtc = null, ReceivedCandlesThisRun = false,
                        QueuedAtUtc = _clock.GetUtcNow(), LastAttemptAtUtc = null, RetryAfterUtc = null, Error = null,
                    };
                    int existing = jobs.FindIndex(j => SameWork(j, next));
                    if (existing >= 0)
                    {
                        CollectionJob previousWork = jobs[existing];
                        next = previousWork.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading
                            ? progressive
                                ? next with { Id = previousWork.Id, IsAutomatic = previousWork.IsAutomatic && next.IsAutomatic }
                                : previousWork with { DiscoveryEmptySessions = empty, IsAvailabilityProbe = true,
                                    RequestedThroughUtc = null, IgnoreKnownGaps = job.IgnoreKnownGaps }
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
            ContinuityGaps = job.Status == CollectionJobStatus.Complete && job.RequestedFromUtc is null && job.RequestedThroughUtc is null
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
