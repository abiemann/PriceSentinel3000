namespace PriceSentinel3000.Application.MarketDataLibrary;

/// <summary>Saved native candles as a share of the full requested trading day.</summary>
public static class CollectionDayCoverage
{
    public static decimal? Calculate(CollectionJob job, IEnumerable<HistoricalDatasetInfo> datasets)
    {
        if (job.SourceIntervalSeconds != 15 || job.SessionBounds is not ("regular" or "extended" or "24_5") ||
            job.SessionDate == DateOnly.MaxValue)
            return null;
        IReadOnlyList<CollectionSessionWindow> sessions = CollectionSchedule.GetSessionWindows(job.SessionDate, job.SessionBounds);
        if (job.RequestedFromUtc is { } requestedFrom && job.RequestedThroughUtc is { } requestedThrough)
            sessions = sessions.Select(session => new CollectionSessionWindow(
                session.FromUtc > requestedFrom ? session.FromUtc : requestedFrom,
                session.ThroughUtc < requestedThrough ? session.ThroughUtc : requestedThrough))
                .Where(session => session.FromUtc < session.ThroughUtc).ToArray();
        if (sessions.Count == 0) return null;

        HistoricalDatasetInfo[] matching = datasets.Where(dataset =>
            string.Equals(dataset.Symbol, job.Symbol, StringComparison.OrdinalIgnoreCase) &&
            dataset.TradingDate == job.SessionDate && dataset.SourceIntervalSeconds == 15 &&
            dataset.AdjustmentPolicy == job.AdjustmentPolicy && dataset.AdjustmentBasis == job.AdjustmentBasis &&
            (job.ProviderInstrumentId is null || dataset.InstrumentId == job.ProviderInstrumentId)).ToArray();
        if (matching.Length == 0) return 0m;
        if (matching.Any(dataset => dataset.SessionBounds is not ("regular" or "extended" or "24_5") ||
                !CollectionSchedule.IsCollectionDate(dataset.TradingDate, dataset.SessionBounds)) ||
            matching.Select(dataset => (dataset.Provider, dataset.InstrumentId)).Distinct().Count() != 1)
            return null;

        const long intervalTicks = 15 * TimeSpan.TicksPerSecond;
        var saved = new List<CollectionSessionWindow>();
        foreach (HistoricalDatasetInfo dataset in matching)
        {
            foreach (CollectionSessionWindow range in CoveredRanges(dataset.Coverage))
            {
                if (range.FromUtc.UtcTicks % intervalTicks != 0 || range.ThroughUtc.UtcTicks % intervalTicks != 0)
                    return null;
                foreach (CollectionSessionWindow session in sessions)
                {
                    DateTimeOffset from = range.FromUtc > session.FromUtc ? range.FromUtc : session.FromUtc;
                    DateTimeOffset through = range.ThroughUtc < session.ThroughUtc ? range.ThroughUtc : session.ThroughUtc;
                    if (through > from) saved.Add(new(from, through));
                }
            }
        }

        long savedTicks = 0;
        DateTimeOffset? previousEnd = null;
        foreach (CollectionSessionWindow range in saved.OrderBy(range => range.FromUtc))
        {
            DateTimeOffset from = previousEnd is { } end && end > range.FromUtc ? end : range.FromUtc;
            if (range.ThroughUtc <= from) continue;
            savedTicks += (range.ThroughUtc - from).Ticks;
            previousEnd = range.ThroughUtc;
        }
        long expectedTicks = sessions.Sum(session => (session.ThroughUtc - session.FromUtc).Ticks);
        return 100m * savedTicks / expectedTicks;
    }

    private static IEnumerable<CollectionSessionWindow> CoveredRanges(HistoricalCoverage coverage)
    {
        if (coverage.ActualCandleCount == 0 || coverage.CoveredFromUtc is not { } from ||
            coverage.CoveredThroughUtc is not { } through) yield break;
        if (from < coverage.RequestedFromUtc) from = coverage.RequestedFromUtc;
        if (through > coverage.RequestedThroughUtc) through = coverage.RequestedThroughUtc;
        foreach (HistoricalGap gap in coverage.Gaps.OrderBy(gap => gap.FromUtc))
        {
            if (gap.ThroughUtc <= from) continue;
            if (gap.FromUtc >= through) break;
            if (gap.FromUtc > from) yield return new(from, gap.FromUtc);
            if (gap.ThroughUtc > from) from = gap.ThroughUtc;
            if (from >= through) yield break;
        }
        if (from < through) yield return new(from, through);
    }
}
