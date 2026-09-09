using System.Globalization;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed record LibraryDaySummary(
    string Symbol,
    DateOnly TradingDate,
    string SourceIntervalDisplay,
    string Provider,
    long? CandleCount,
    decimal? CoveragePercent,
    string CoverageDetails)
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static IReadOnlyList<LibraryDaySummary> Create(
        IEnumerable<HistoricalDatasetInfo> datasets, DateTimeOffset? now = null)
    {
        DateTimeOffset atUtc = now ?? DateTimeOffset.UtcNow;
        return datasets.GroupBy(dataset => (dataset.Symbol, dataset.TradingDate))
            .OrderByDescending(group => group.Key.TradingDate)
            .ThenBy(group => group.Key.Symbol, StringComparer.Ordinal)
            .Select(group => Summarize(group.ToArray(), atUtc)).ToArray();
    }

    private static LibraryDaySummary Summarize(HistoricalDatasetInfo[] datasets, DateTimeOffset now)
    {
        HistoricalDatasetInfo first = datasets[0];
        int[] intervals = datasets.Select(dataset => dataset.SourceIntervalSeconds).Distinct().ToArray();
        string[] providers = datasets.Select(dataset => dataset.Provider).Distinct(StringComparer.Ordinal).ToArray();
        string intervalDisplay = intervals.Length == 1
            ? intervals[0].ToString(CultureInfo.CurrentCulture) : "Mixed";
        string providerDisplay = providers.Length == 1 ? providers[0] : "Mixed";
        string fileSummary = $"{datasets.Length.ToString("N0", CultureInfo.CurrentCulture)} saved files. ";
        const string replayDetails = "Overlapping saved periods count once. Replay checks source and price compatibility separately.";

        LibraryDaySummary Unavailable(string reason) => new(first.Symbol, first.TradingDate,
            intervalDisplay, providerDisplay, null, null, fileSummary + reason + " " + replayDetails);

        if (intervals.Length != 1)
            return Unavailable("Multiple source intervals are present; coarser candles are not counted as finer candles.");
        if (datasets.Select(dataset => (dataset.Provider, dataset.InstrumentId,
                dataset.AdjustmentPolicy, dataset.AdjustmentBasis)).Distinct().Count() != 1)
            return Unavailable("Different providers, instruments, or adjustment bases are present; their coverage is not combined.");
        if (intervals[0] is not (15 or 30 or 60 or 120) || datasets.Any(dataset =>
                dataset.SessionBounds is not ("regular" or "extended" or "24_5") ||
                !CollectionSchedule.IsCollectionDate(dataset.TradingDate, dataset.SessionBounds)))
            return Unavailable("Coverage is unavailable for these sessions, dates, or source intervals.");

        string bounds = datasets.Any(dataset => dataset.SessionBounds == "24_5") ? "24_5" :
            datasets.Any(dataset => dataset.SessionBounds == "extended") ? "extended" : "regular";
        IReadOnlyList<CollectionSessionWindow> sessions = CollectionSchedule.GetSessionWindows(first.TradingDate, bounds);
        long intervalTicks = intervals[0] * TimeSpan.TicksPerSecond;
        var saved = new List<CollectionSessionWindow>();
        foreach (HistoricalDatasetInfo dataset in datasets)
        {
            foreach (CollectionSessionWindow range in CoveredRanges(dataset.Coverage))
            {
                // Require the native interval's UTC grid before inferring candle
                // counts from overlapping covered ranges.
                if (range.FromUtc.UtcTicks % intervalTicks != 0 || range.ThroughUtc.UtcTicks % intervalTicks != 0)
                    return Unavailable("Saved candle boundaries do not share the native interval grid.");
                foreach (CollectionSessionWindow session in sessions)
                {
                    DateTimeOffset from = range.FromUtc > session.FromUtc ? range.FromUtc : session.FromUtc;
                    DateTimeOffset through = range.ThroughUtc < session.ThroughUtc ? range.ThroughUtc : session.ThroughUtc;
                    if (through > from) saved.Add(new(from, through));
                }
            }
        }

        // Only whole native candles whose end has passed participate in coverage.
        // The stored-candle total stays unchanged as the clock advances.
        long cutoffTicks = now.UtcTicks - now.UtcTicks % intervalTicks;
        long savedTicks = 0, completedSavedTicks = 0;
        DateTimeOffset? previousEnd = null;
        foreach (CollectionSessionWindow range in saved.OrderBy(range => range.FromUtc))
        {
            DateTimeOffset from = previousEnd is { } end && end > range.FromUtc ? end : range.FromUtc;
            if (range.ThroughUtc <= from) continue;
            savedTicks += (range.ThroughUtc - from).Ticks;
            completedSavedTicks += Math.Max(0, Math.Min(range.ThroughUtc.UtcTicks, cutoffTicks) - from.UtcTicks);
            previousEnd = range.ThroughUtc;
        }
        long expectedTicks = sessions.Sum(session =>
            Math.Max(0, Math.Min(session.ThroughUtc.UtcTicks, cutoffTicks) - session.FromUtc.UtcTicks));
        long count = savedTicks / intervalTicks;
        long completedSaved = completedSavedTicks / intervalTicks;
        long expected = expectedTicks / intervalTicks;
        string sessionLabel = bounds == "24_5" ? "all-hours" : bounds;
        string period = cutoffTicks >= sessions[^1].ThroughUtc.UtcTicks ? $"the full {sessionLabel} day" :
            expected == 0 ? $"the {sessionLabel} day" :
            $"the {sessionLabel} day through " +
                $"{TimeZoneInfo.ConvertTime(new DateTimeOffset(cutoffTicks, TimeSpan.Zero), Eastern):HH:mm:ss} Eastern";
        string details = expected == 0
            ? $"No completed {intervalDisplay}-second candles are expected for {period} yet. "
            : $"{completedSaved.ToString("N0", CultureInfo.CurrentCulture)} of " +
                $"{expected.ToString("N0", CultureInfo.CurrentCulture)} {intervalDisplay}-second candles saved for {period}. ";
        details += $"Eastern calendar date {first.TradingDate:yyyy-MM-dd}. " +
            "Only completed candles through the cutoff count; market closures and future candles are excluded. " +
            $"{count.ToString("N0", CultureInfo.CurrentCulture)} stored candles. " + fileSummary + replayDetails;
        return new(first.Symbol, first.TradingDate, intervalDisplay, providerDisplay,
            count, expectedTicks == 0 ? null : 100m * completedSavedTicks / expectedTicks, details);
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
