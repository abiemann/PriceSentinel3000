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
        GetGapIndex(_state.Settings.LibraryRootPath);
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

        // A new run supersedes speculative queued dates. Older work is discovered
        // one date at a time; terminal history and explicitly queued work remain.
        var jobs = _state.Jobs.Where(j => !(j.Status == CollectionJobStatus.Pending && j.IsAvailabilityProbe &&
            j.SourceIntervalSeconds == 15 && j.AdjustmentPolicy == "split" &&
            j.AdjustmentBasis == "robinhood-split-unversioned" &&
            j.SessionBounds is "regular" or "extended" or "24_5" &&
            SamePath(j.LibraryRootPath, _state.Settings.LibraryRootPath) && members.Any(m => m.Symbol == j.Symbol &&
                (m.ProviderInstrumentId is null || j.ProviderInstrumentId is null ||
                    m.ProviderInstrumentId == j.ProviderInstrumentId)))).ToList();
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
                IsAvailabilityProbe = true,
                IgnoreKnownGaps = ignoreKnownGaps,
                RequestedThroughUtc = partialThrough,
                DiscoveryAsOfDate = today,
                AvailabilityCheckPending = latest < today,
                DiscoveryEmptySessions = 0,
                NextGapFromUtc = null,
                ReceivedCandlesThisRun = false,
            };
        }
        Commit(_state with { Jobs = jobs.ToArray() });
    }

    // Advancing the frontier and finishing its job are one durable state write. A crash
    // cannot leave successful discovery permanently stopped between two sessions.
    private void FinishCollection(CollectionJob job, bool emptySession = false, bool receivedCandles = false)
    {
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
