namespace PriceSentinel3000.Core.MarketData;

/// <summary>
/// Evaluates the published weekly equity-session schedule in New York time.
/// Exchange holidays, unscheduled closures, symbol halts, and broker restrictions
/// are not represented and must be checked separately.
/// </summary>
public sealed class EquityMarketSessionEvaluator(TimeProvider? timeProvider = null)
{
    private static readonly TimeOnly RegularOpen = new(9, 30);
    private static readonly TimeOnly RegularClose = new(16, 0);
    private static readonly TimeOnly ExtendedHoursOpen = new(4, 0);
    private static readonly TimeOnly ExtendedHoursClose = new(20, 0);
    private static readonly TimeOnly OvernightWeeklyBoundary = new(20, 0);
    private static readonly TimeZoneInfo NewYorkTimeZone =
        ResolveNewYorkTimeZone();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool IsTradableNow(
        bool isExtendedHoursEligible,
        bool isOvernightEligible) =>
        IsTradableAt(
            _timeProvider.GetUtcNow(),
            isExtendedHoursEligible,
            isOvernightEligible);

    public static bool IsTradableAt(
        DateTimeOffset timestamp,
        bool isExtendedHoursEligible,
        bool isOvernightEligible)
    {
        DateTimeOffset newYork = TimeZoneInfo.ConvertTime(timestamp, NewYorkTimeZone);
        TimeOnly localTime = TimeOnly.FromDateTime(newYork.DateTime);

        if (isOvernightEligible)
        {
            return newYork.DayOfWeek switch
            {
                DayOfWeek.Sunday => localTime >= OvernightWeeklyBoundary,
                >= DayOfWeek.Monday and <= DayOfWeek.Thursday => true,
                DayOfWeek.Friday => localTime < OvernightWeeklyBoundary,
                _ => false,
            };
        }

        if (newYork.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        return isExtendedHoursEligible
            ? localTime >= ExtendedHoursOpen && localTime < ExtendedHoursClose
            : localTime >= RegularOpen && localTime < RegularClose;
    }

    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }
}
