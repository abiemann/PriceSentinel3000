using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record CollectionSessionWindow(DateTimeOffset FromUtc, DateTimeOffset ThroughUtc);
public sealed record DueCollectionSession(DateTimeOffset OccurrenceUtc, DateOnly SessionDate);

public static class CollectionSchedule
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static CollectionSessionWindow GetSessionWindow(DateOnly date, string bounds = "regular")
    {
        IReadOnlyList<CollectionSessionWindow> windows = GetSessionWindows(date, bounds);
        if (windows.Count == 0) throw new ArgumentException("The date is not an equity collection session.");
        return new(windows[0].FromUtc, windows[^1].ThroughUtc);
    }

    public static bool IsCollectionDate(DateOnly date, string bounds = "regular") =>
        GetSessionWindows(date, bounds).Count > 0;

    /// <summary>Active intervals within the candle's Eastern calendar date, including the next trading day's evening.</summary>
    public static IReadOnlyList<CollectionSessionWindow> GetSessionWindows(DateOnly date, string bounds = "regular")
    {
        if (bounds is not ("regular" or "extended" or "24_5")) throw new ArgumentException("Unknown session coverage.");
        bool trading = UsEquityTradingCalendar.IsTradingDay(date);
        bool early = UsEquityTradingCalendar.IsEarlyClose(date);
        if (bounds == "24_5")
        {
            var windows = new List<CollectionSessionWindow>(2);
            if (trading)
                windows.Add(new(ToUtc(date.ToDateTime(TimeOnly.MinValue), Eastern),
                    ToUtc(date.ToDateTime(new(early ? 17 : 20, 0)), Eastern)));
            DateOnly next = date.AddDays(1);
            if (UsEquityTradingCalendar.IsTradingDay(next))
            {
                var evening = new CollectionSessionWindow(ToUtc(date.ToDateTime(new(20, 0)), Eastern),
                    ToUtc(next.ToDateTime(TimeOnly.MinValue), Eastern));
                if (windows.Count > 0 && windows[^1].ThroughUtc == evening.FromUtc)
                    windows[^1] = windows[^1] with { ThroughUtc = evening.ThroughUtc };
                else windows.Add(evening);
            }
            return windows;
        }
        if (!trading) return [];
        TimeOnly start = bounds == "regular" ? new(9, 30) : new(4, 0);
        TimeOnly end = bounds == "regular" ? new(early ? 13 : 16, 0) : new(early ? 17 : 20, 0);
        return [new(ToUtc(date.ToDateTime(start), Eastern), ToUtc(date.ToDateTime(end), Eastern))];
    }

    public static DateOnly LatestFinalizedSession(DateTimeOffset atUtc, string bounds, int finalizationDelayMinutes)
    {
        DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(atUtc, Eastern).DateTime);
        for (int i = 0; i < 14; i++, date = date.AddDays(-1))
            if (IsCollectionDate(date, bounds) &&
                GetSessionWindow(date, bounds).ThroughUtc.AddMinutes(finalizationDelayMinutes) <= atUtc)
                return date;
        throw new InvalidOperationException("No finalized market session was found.");
    }

    /// <summary>Skipped clocks run at the first valid minute; repeated clocks run once at the earlier UTC occurrence.</summary>
    public static DateTimeOffset ResolveDailyOccurrence(DateOnly date, TimeOnly time, string timeZoneId)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);
        for (int i = 0; zone.IsInvalidTime(local); i++)
        {
            if (i >= 180) throw new InvalidOperationException("The chosen time zone has an unsupported clock transition.");
            local = local.AddMinutes(1);
        }
        return ToUtc(local, zone);
    }

    public static IReadOnlyList<DueCollectionSession> GetDueSessions(
        CollectionSettings settings, DateTimeOffset? lastOccurrenceUtc, DateTimeOffset nowUtc)
    {
        if (!settings.AutomaticDownloadsEnabled || settings.AutomaticEnabledAtUtc is not { } enabledAt || nowUtc < enabledAt)
            return [];
        DateTimeOffset earliest = nowUtc.AddDays(-settings.CatchUpCalendarDays);
        if (enabledAt > earliest) earliest = enabledAt;
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        DateOnly first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(earliest, zone).DateTime);
        DateOnly last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var result = new List<DueCollectionSession>();
        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            DateTimeOffset occurrence = ResolveDailyOccurrence(day, settings.DailyDownloadTime, settings.TimeZoneId);
            if (occurrence < earliest || occurrence > nowUtc || occurrence <= lastOccurrenceUtc) continue;
            DateOnly session = LatestFinalizedSession(occurrence, settings.SessionBounds, settings.ProviderFinalizationDelayMinutes);
            if (GetSessionWindow(session, settings.SessionBounds).ThroughUtc < nowUtc.AddDays(-settings.CatchUpCalendarDays)) continue;
            result.Add(new(occurrence, session));
        }
        return result;
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) => zone.IsAmbiguousTime(local)
        ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
        : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
}
