using System.Globalization;
using System.IO;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public enum LibraryCoverageBlockState { Complete, Partial, Missing, Closed, Future }

public sealed record LibraryCoverageBlock(
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    int SavedCandleCount,
    int ExpectedCandleCount,
    LibraryCoverageBlockState State,
    string ToolTip);

public sealed record LibraryCoverageTick(double Position, string Label);

public sealed record LibraryCoverageTimeline(
    string Symbol,
    DateOnly Date,
    string TimeZoneLabel,
    string RangeLabel,
    string Summary,
    string? Notice,
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    IReadOnlyList<LibraryCoverageBlock> Blocks,
    IReadOnlyList<LibraryCoverageTick> Ticks)
{
    private const long CandleTicks = 15 * TimeSpan.TicksPerSecond;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static LibraryCoverageTimeline Create(
        string symbol, DateOnly date, TimeZoneInfo timezone, bool? overnight,
        IEnumerable<HistoricalDatasetInfo> datasets, DateTimeOffset now)
    {
        bool fullDay = overnight != false;
        DateTimeOffset from = CollectionSchedule.ResolveDailyOccurrence(date, new(fullDay ? 0 : 6, 0), timezone.Id);
        DateTimeOffset through = CollectionSchedule.ResolveDailyOccurrence(
            fullDay ? date.AddDays(1) : date, new(fullDay ? 0 : 17, 0), timezone.Id);
        var saved = new Dictionary<long, (string Provider, string Instrument, string Policy, string Basis)>();
        bool observedOvernight = false;
        foreach (HistoricalDatasetInfo dataset in datasets.Where(dataset =>
            string.Equals(dataset.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            dataset.SourceIntervalSeconds == 15 &&
            dataset.Coverage.RequestedFromUtc < through && dataset.Coverage.RequestedThroughUtc > from))
        {
            var identity = (dataset.Provider, dataset.InstrumentId, dataset.AdjustmentPolicy, dataset.AdjustmentBasis);
            foreach (CollectionSessionWindow range in CoveredRanges(dataset.Coverage))
            {
                if (range.FromUtc.UtcTicks % CandleTicks != 0 || range.ThroughUtc.UtcTicks % CandleTicks != 0)
                    throw new InvalidDataException("Saved candle boundaries do not match the 15-second grid.");
                long first = Math.Max(range.FromUtc.UtcTicks, from.UtcTicks);
                long last = Math.Min(range.ThroughUtc.UtcTicks, through.UtcTicks);
                for (long slot = first; slot < last; slot += CandleTicks)
                {
                    if (saved.TryGetValue(slot, out var previous) && previous != identity)
                        throw new InvalidDataException(
                            "Overlapping saved candles use different providers, instruments, or price adjustment bases.");
                    saved[slot] = identity;
                    int easternHour = TimeZoneInfo.ConvertTime(new DateTimeOffset(slot, TimeSpan.Zero), Eastern).Hour;
                    observedOvernight |= easternHour < 4 || easternHour >= 20;
                }
            }
        }

        var sessions = new List<CollectionSessionWindow>();
        DateOnly firstEastern = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, Eastern).DateTime);
        DateOnly lastEastern = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(through.AddTicks(-1), Eastern).DateTime);
        for (DateOnly day = firstEastern; day <= lastEastern; day = day.AddDays(1))
            sessions.AddRange(CollectionSchedule.GetSessionWindows(day, fullDay ? "24_5" : "extended"));

        var blocks = new List<LibraryCoverageBlock>();
        for (DateTimeOffset start = from; start < through; start = start.AddMinutes(15))
        {
            DateTimeOffset end = start.AddMinutes(15) < through ? start.AddMinutes(15) : through;
            int possible = 0, expected = 0, actual = 0;
            for (long slot = start.UtcTicks; slot + CandleTicks <= end.UtcTicks; slot += CandleTicks)
            {
                if (!sessions.Any(session => slot >= session.FromUtc.UtcTicks &&
                    slot + CandleTicks <= session.ThroughUtc.UtcTicks)) continue;
                possible++;
                if (slot + CandleTicks > now.UtcTicks) continue;
                expected++;
                if (saved.ContainsKey(slot)) actual++;
            }
            LibraryCoverageBlockState state = possible == 0 ? LibraryCoverageBlockState.Closed :
                expected == 0 ? LibraryCoverageBlockState.Future :
                actual == expected ? LibraryCoverageBlockState.Complete :
                actual == 0 ? LibraryCoverageBlockState.Missing : LibraryCoverageBlockState.Partial;
            string times = $"{LocalTime(start, timezone)}–{LocalTime(end, timezone)}";
            string details = state switch
            {
                LibraryCoverageBlockState.Closed => "Market closed. No candles expected.",
                LibraryCoverageBlockState.Future => "No completed candles expected yet.",
                _ => $"{actual.ToString("N0", CultureInfo.CurrentCulture)} of " +
                    $"{expected.ToString("N0", CultureInfo.CurrentCulture)} completed 15-second candles saved " +
                    $"({(100m * actual / expected).ToString("0.##", CultureInfo.CurrentCulture)}%). " +
                    $"{(expected - actual).ToString("N0", CultureInfo.CurrentCulture)} missing.",
            };
            if (expected > 0 && expected < possible)
                details += " This block is still in progress; future candles are excluded.";
            blocks.Add(new(start, end, actual, expected, state, times + ": " + details));
        }

        var ticks = new List<LibraryCoverageTick>();
        for (DateTimeOffset at = from; at <= through; at = at.AddMinutes(15))
        {
            DateTimeOffset local = TimeZoneInfo.ConvertTime(at, timezone);
            if (at != through && (local.Minute != 0 || fullDay && local.Hour % 2 != 0)) continue;
            string label = fullDay && at == through ? "24:00" : local.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (timezone.IsAmbiguousTime(local.DateTime)) label += $" ({local:zzz})";
            ticks.Add(new((at - from).TotalSeconds / (through - from).TotalSeconds, label));
        }
        DateTimeOffset midday = TimeZoneInfo.ConvertTime(from.AddTicks((through - from).Ticks / 2), timezone);
        string zoneName = timezone.IsDaylightSavingTime(midday) ? timezone.DaylightName : timezone.StandardName;
        string zoneLabel = $"{zoneName} (UTC{midday:zzz})";
        int totalSaved = blocks.Sum(block => block.SavedCandleCount);
        int totalExpected = blocks.Sum(block => block.ExpectedCandleCount);
        string summary = totalExpected == 0
            ? "No completed trading candles are expected in this time range."
            : $"{totalSaved.ToString("N0", CultureInfo.CurrentCulture)} of " +
                $"{totalExpected.ToString("N0", CultureInfo.CurrentCulture)} completed 15-second candles saved " +
                $"({(100m * totalSaved / totalExpected).ToString("0.##", CultureInfo.CurrentCulture)}%).";
        summary += " Market closures and future candles are excluded.";
        string? notice = overnight is null && !observedOvernight
            ? "24-hour eligibility is unknown. The full local day is shown so overnight gaps are not hidden."
            : null;
        return new(symbol, date, zoneLabel,
            $"{date:yyyy-MM-dd} · {(fullDay ? "00:00–24:00" : "06:00–17:00")} local time",
            summary, notice, from, through, blocks.AsReadOnly(), ticks.AsReadOnly());
    }

    private static string LocalTime(DateTimeOffset at, TimeZoneInfo timezone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(at, timezone);
        return timezone.IsAmbiguousTime(local.DateTime)
            ? local.ToString("HH:mm (zzz)", CultureInfo.CurrentCulture)
            : local.ToString("HH:mm", CultureInfo.CurrentCulture);
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
