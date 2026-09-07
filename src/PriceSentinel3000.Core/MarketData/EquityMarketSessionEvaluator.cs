namespace PriceSentinel3000.Core.MarketData;

/// <summary>
/// Evaluates the scheduled equity sessions in New York time, including recurring
/// exchange holidays and early closes. Unscheduled closures, symbol halts, and
/// broker restrictions must be checked separately.
/// </summary>
public sealed class EquityMarketSessionEvaluator(TimeProvider? timeProvider = null)
{
    private static readonly TimeOnly RegularOpen = new(9, 30);
    private static readonly TimeOnly RegularClose = new(16, 0);
    private static readonly TimeOnly ExtendedHoursOpen = new(4, 0);
    private static readonly TimeOnly ExtendedHoursClose = new(20, 0);
    private static readonly TimeOnly EarlyRegularClose = new(13, 0);
    private static readonly TimeOnly EarlyExtendedHoursClose = new(17, 0);
    private static readonly TimeOnly OvernightOpen = new(20, 0);
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
        DateOnly localDate = DateOnly.FromDateTime(newYork.DateTime);
        TimeOnly localTime = TimeOnly.FromDateTime(newYork.DateTime);

        // Robinhood's 20:00-midnight session belongs to the following trading
        // date. A holiday evening can reopen, but the evening before it cannot.
        // https://cdn.robinhood.com/assets/robinhood/legal/ExtendedHoursTradingDisclosure.pdf
        if (isOvernightEligible && localTime >= OvernightOpen)
        {
            return UsEquityTradingCalendar.IsTradingDay(localDate.AddDays(1));
        }

        if (!UsEquityTradingCalendar.IsTradingDay(localDate))
        {
            return false;
        }

        bool earlyClose = UsEquityTradingCalendar.IsEarlyClose(localDate);
        TimeOnly extendedClose = earlyClose ? EarlyExtendedHoursClose : ExtendedHoursClose;
        if (isOvernightEligible)
        {
            return localTime < extendedClose;
        }

        return isExtendedHoursEligible
            ? localTime >= ExtendedHoursOpen && localTime < extendedClose
            : localTime >= RegularOpen && localTime < (earlyClose ? EarlyRegularClose : RegularClose);
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
