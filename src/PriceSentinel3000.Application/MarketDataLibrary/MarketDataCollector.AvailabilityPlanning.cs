namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed partial class MarketDataCollector
{
    private sealed record AvailabilityDayEligibility(
        bool HasSavedCandles, bool HasRequestableGaps, bool EntireDayObservedEmpty, bool EntireDayUnavailable);

    private AvailabilityDayEligibility CheckOlderAvailabilityDay(CollectionJob job)
    {
        CollectionSessionWindow day = CollectionSchedule.GetSessionWindow(job.SessionDate, job.SessionBounds);
        HistoricalDataQueryResult saved = GetCollectionLibrary(job.LibraryRootPath).Query(new(job.Symbol,
            day.FromUtc, day.ThroughUtc, 15, AdjustmentPolicy: job.AdjustmentPolicy,
            AdjustmentBasis: job.AdjustmentBasis, SessionBounds: job.SessionBounds,
            RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage, IncludeCompatibleSessions: true));
        if (!saved.Succeeded || job.ProviderInstrumentId is not null &&
            saved.Datasets.Any(dataset => dataset.InstrumentId != job.ProviderInstrumentId))
            throw new InvalidDataException("Saved history could not be validated before queuing older downloads.");
        HistoricalGap[] missing = CollectionSchedule.GetSessionWindows(job.SessionDate, job.SessionBounds)
            .SelectMany(window => saved.Coverage.Gaps.Select(gap => new HistoricalGap(
                gap.FromUtc > window.FromUtc ? gap.FromUtc : window.FromUtc,
                gap.ThroughUtc < window.ThroughUtc ? gap.ThroughUtc : window.ThroughUtc)))
            .Where(gap => gap.FromUtc < gap.ThroughUtc).OrderBy(gap => gap.FromUtc).ToArray();
        ICollectionGapIndex? index = GetGapIndex(job.LibraryRootPath);
        if (saved.Candles.Count > 0)
            index?.ResolveSavedRanges(GapKey(job), SavedCandleRanges(saved.Candles, day.FromUtc, day.ThroughUtc));
        CollectionGapSnapshot known = index?.Query(
            GapKey(job), day.FromUtc, day.ThroughUtc, _clock.GetUtcNow()) ?? new([], false);
        HistoricalGap[] exhausted = known.AttemptedRanges.Where(attempt => attempt.Attempts >= 2)
            .Select(attempt => attempt.Gap).OrderBy(gap => gap.FromUtc).ToArray();
        bool hasAttemptsLeft = ExcludeKnownGaps(missing, exhausted).Length > 0;
        bool entirelyEmpty = missing.Length > 0 && saved.Candles.Count == 0 &&
            ExcludeKnownGaps(missing, known.UnavailableRanges).Length == 0;
        return new(saved.Candles.Count > 0,
            job.IgnoreKnownGaps ? missing.Length > 0 : hasAttemptsLeft, entirelyEmpty, entirelyEmpty && !hasAttemptsLeft);
    }

    private CollectionJob AvailabilityJob(CollectionAvailabilityRun run, DownloadListMember member, DateOnly date) => new()
    {
        Symbol = member.Symbol, ProviderInstrumentId = member.ProviderInstrumentId, SessionDate = date,
        SessionBounds = CollectionSettings.AllAvailableSessionBounds, LibraryRootPath = run.LibraryRootPath,
        QueuedAtUtc = _clock.GetUtcNow(), IsAutomatic = run.IsAutomatic, IsAvailabilityProbe = true,
        IgnoreKnownGaps = run.IgnoreKnownGaps, DiscoveryAsOfDate = run.AsOfDate, AvailabilityRunId = run.Id,
        AvailabilityCheckPending = date < run.AsOfDate, DiscoveryEmptySessions = 0,
    };

    private static Guid QueueAvailabilityJob(List<CollectionJob> jobs, CollectionJob candidate)
    {
        // A separately requested pending range keeps its exact work and retry state.
        CollectionJob? explicitWork = jobs.FirstOrDefault(job => !job.IsAvailabilityProbe &&
            job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading && SameWork(job, candidate));
        if (explicitWork is not null) return explicitWork.Id;
        int existing = jobs.FindIndex(job => job.IsAvailabilityProbe && SameWork(job, candidate));
        if (existing >= 0)
        {
            candidate = candidate with { Id = jobs[existing].Id };
            jobs[existing] = candidate;
        }
        else
        {
            if (jobs.Count(job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading) >= 10_000)
                throw new InvalidOperationException("The pending queue is limited to 10,000 downloads. Complete queued work before adding another range.");
            jobs.Add(candidate);
        }
        return candidate.Id;
    }

    // All tickers finish the current date before the frontier moves to an older date.
    // Return true when another tick should keep planning, even if no visible row was needed.
    private bool AdvanceAvailabilityDiscovery()
    {
        CollectionAvailabilityRun? run = _state.AvailabilityRun;
        if (run is null || run.IsAutomatic && !_state.Settings.AutomaticDownloadsEnabled) return false;
        CollectionJob[] current = _state.Jobs.Where(job => run.CurrentJobIds.Contains(job.Id)).ToArray();
        if (current.Any(job => job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading or CollectionJobStatus.Failed))
            return false;
        var jobs = _state.Jobs.ToList();
        var members = run.Members.ToList();
        var currentJobs = new List<Guid>();
        if (run.CurrentDate < run.AsOfDate)
        {
            foreach (DownloadListMember member in members.ToArray())
            {
                CollectionJob candidate = AvailabilityJob(run, member, run.CurrentDate);
                if (!current.Any(job => SameWork(job, candidate))) continue;
                AvailabilityDayEligibility eligibility = CheckOlderAvailabilityDay(candidate);
                if (eligibility.EntireDayUnavailable) members.Remove(member);
                else if (eligibility.HasRequestableGaps && (!run.IgnoreKnownGaps || eligibility.EntireDayObservedEmpty))
                    currentJobs.Add(QueueAvailabilityJob(jobs, candidate));
            }
            if (currentJobs.Count > 0)
            {
                Commit(_state with { Jobs = jobs.ToArray(), AvailabilityRun = run with
                    { Members = members.ToArray(), CurrentJobIds = currentJobs.ToArray() } });
                return true;
            }
        }
        if (members.Count == 0)
        {
            Commit(_state with { AvailabilityRun = null });
            return false;
        }

        // Bound one planning pass so long runs of complete or capped saved days do
        // not monopolize the collector. The next tick resumes the persisted date.
        for (int dates = 0; dates < 16; dates++)
        {
            if (run.CurrentDate <= new DateOnly(1900, 1, 1))
            {
                Commit(_state with { AvailabilityRun = null });
                return false;
            }
            run = run with { CurrentDate = run.CurrentDate.AddDays(-1), CurrentJobIds = [] };
            if (!CollectionSchedule.IsCollectionDate(run.CurrentDate, CollectionSettings.AllAvailableSessionBounds)) continue;
            foreach (DownloadListMember member in members.ToArray())
            {
                CollectionJob candidate = AvailabilityJob(run, member, run.CurrentDate);
                AvailabilityDayEligibility eligibility = CheckOlderAvailabilityDay(candidate);
                if (!run.IgnoreKnownGaps && eligibility.EntireDayUnavailable) members.Remove(member);
                else if (eligibility.HasRequestableGaps) currentJobs.Add(QueueAvailabilityJob(jobs, candidate));
            }
            if (members.Count == 0 || currentJobs.Count > 0)
            {
                Commit(_state with
                {
                    Jobs = jobs.ToArray(),
                    AvailabilityRun = members.Count == 0 ? null : run with
                        { Members = members.ToArray(), CurrentJobIds = currentJobs.ToArray() },
                });
                return currentJobs.Count > 0;
            }
        }
        Commit(_state with { AvailabilityRun = run with { Members = members.ToArray() } });
        return true;
    }
}
