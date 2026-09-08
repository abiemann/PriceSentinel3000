using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record CollectionBackfillGap(DateOnly FromSessionDate, DateOnly ThroughSessionDate);
public sealed record CollectionBackfillPlan(
    IReadOnlyList<DateOnly> MissingSessions,
    IReadOnlyList<CollectionBackfillGap> ExpiredGaps);

/// <summary>Plans from validated portable files, independently of the machine that collected them.</summary>
public static class CollectionBackfillPlanner
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static CollectionBackfillPlan Plan(DownloadListMember member,
        IReadOnlyList<HistoricalDatasetInfo> datasets, DateOnly latestFinalizedSession,
        string sessionBounds, DateTimeOffset nowUtc, int catchUpCalendarDays = 7,
        DateOnly? trackingFromSessionDate = null)
    {
        if (catchUpCalendarDays is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(catchUpCalendarDays));
        _ = CollectionSchedule.GetSessionWindow(latestFinalizedSession, sessionBounds);
        string symbol = CollectionSettings.NormalizeSymbol(member.Symbol);
        HistoricalDatasetInfo[] matching = datasets.Where(d => d.Symbol == symbol &&
            d.TradingDate <= latestFinalizedSession && d.SessionBounds == sessionBounds &&
            d.AdjustmentPolicy == "split" && d.AdjustmentBasis == "robinhood-split-unversioned" &&
            (member.ProviderInstrumentId is null || d.InstrumentId == member.ProviderInstrumentId)).ToArray();

        // A newer partial revision is still a gap, even if an older copy was complete.
        // Different identities for one day cannot prove a single continuous source.
        DateOnly[] completeDays = matching.Where(d => d.SourceIntervalSeconds == 15)
            .GroupBy(d => d.TradingDate)
            .Where(g => g.Select(d => (d.Provider, d.InstrumentId)).Distinct().Count() == 1)
            .Select(g => g.OrderByDescending(d => d.FetchedAtUtc)
                .ThenBy(d => d.DatasetHash, StringComparer.Ordinal).First())
            .Where(d => IsCompleteSession(d, sessionBounds)).Select(d => d.TradingDate).Order().ToArray();
        var complete = completeDays.ToHashSet();

        DateTimeOffset retryCutoffUtc = nowUtc.AddDays(-catchUpCalendarDays);
        DateOnly retryFrom = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(retryCutoffUtc, Eastern).DateTime);
        while (retryFrom <= latestFinalizedSession && (!UsEquityTradingCalendar.IsTradingDay(retryFrom) ||
            CollectionSchedule.GetSessionWindow(retryFrom, sessionBounds).ThroughUtc < retryCutoffUtc))
            retryFrom = retryFrom.AddDays(1);

        // Coarse files establish when tracking began, but never satisfy 15-second coverage.
        DateOnly first = matching.Length > 0 ? matching.Min(d => d.TradingDate) : trackingFromSessionDate ?? retryFrom;
        if (trackingFromSessionDate is { } tracked && tracked < first) first = tracked;
        var missing = new List<DateOnly>();
        for (DateOnly day = first > retryFrom ? first : retryFrom; day <= latestFinalizedSession; day = day.AddDays(1))
            if (UsEquityTradingCalendar.IsTradingDay(day) && !complete.Contains(day)) missing.Add(day);

        // Compress old holes around complete days. Years offline do not create years of
        // network jobs or require iterating every old date on each scheduled run.
        var expired = new List<CollectionBackfillGap>();
        DateOnly through = retryFrom.AddDays(-1);
        if (through > latestFinalizedSession) through = latestFinalizedSession;
        DateOnly cursor = first;
        foreach (DateOnly saved in completeDays.Where(d => d >= first && d <= through))
        {
            AddGap(cursor, saved.AddDays(-1), expired);
            cursor = saved.AddDays(1);
        }
        AddGap(cursor, through, expired);
        return new(missing.ToArray(), expired.ToArray());
    }

    private static bool IsCompleteSession(HistoricalDatasetInfo dataset, string bounds)
    {
        if (!UsEquityTradingCalendar.IsTradingDay(dataset.TradingDate)) return false;
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(dataset.TradingDate, bounds);
        HistoricalCoverage coverage = dataset.Coverage;
        return coverage.Complete && coverage.Gaps.Count == 0 &&
            coverage.RequestedFromUtc <= window.FromUtc && coverage.RequestedThroughUtc >= window.ThroughUtc &&
            coverage.CoveredFromUtc <= window.FromUtc && coverage.CoveredThroughUtc >= window.ThroughUtc;
    }

    private static void AddGap(DateOnly from, DateOnly through, List<CollectionBackfillGap> gaps)
    {
        while (from <= through && !UsEquityTradingCalendar.IsTradingDay(from)) from = from.AddDays(1);
        while (through >= from && !UsEquityTradingCalendar.IsTradingDay(through)) through = through.AddDays(-1);
        if (from <= through) gaps.Add(new(from, through));
    }
}
