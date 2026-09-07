namespace PriceSentinel3000.Core.MarketData;

/// <summary>
/// Recurring U.S. equity exchange holidays and early closes under the current
/// NYSE calendar rules. One-off or unscheduled closures are not represented.
/// See https://www.nyse.com/trade/hours-calendars.
/// </summary>
public static class UsEquityTradingCalendar
{
    public static bool IsTradingDay(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        int year = date.Year;
        var newYear = new DateOnly(year, 1, 1);
        // Unlike other fixed holidays, a Saturday New Year's Day is not
        // observed on the preceding Friday by the equity exchanges.
        if (newYear.DayOfWeek == DayOfWeek.Sunday)
        {
            newYear = newYear.AddDays(1);
        }

        return date != newYear &&
            (year < 1998 || date != NthWeekday(year, 1, DayOfWeek.Monday, 3)) &&
            date != NthWeekday(year, 2, DayOfWeek.Monday, 3) &&
            date != EasterSunday(year).AddDays(-2) &&
            date != LastMondayInMay(year) &&
            (year < 2022 || date != ObservedHoliday(new DateOnly(year, 6, 19))) &&
            date != ObservedHoliday(new DateOnly(year, 7, 4)) &&
            date != NthWeekday(year, 9, DayOfWeek.Monday, 1) &&
            date != NthWeekday(year, 11, DayOfWeek.Thursday, 4) &&
            date != ObservedHoliday(new DateOnly(year, 12, 25));
    }

    public static bool IsEarlyClose(DateOnly date) =>
        IsTradingDay(date) &&
        ((date.Month == 7 && date.Day == 3) ||
         (date.Month == 12 && date.Day == 24) ||
         date == NthWeekday(date.Year, 11, DayOfWeek.Thursday, 4).AddDays(1));

    private static DateOnly ObservedHoliday(DateOnly holiday) =>
        holiday.DayOfWeek switch
        {
            DayOfWeek.Saturday => holiday.AddDays(-1),
            DayOfWeek.Sunday => holiday.AddDays(1),
            _ => holiday,
        };

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int occurrence)
    {
        var first = new DateOnly(year, month, 1);
        int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (occurrence - 1) * 7);
    }

    private static DateOnly LastMondayInMay(int year)
    {
        var last = new DateOnly(year, 5, 31);
        return last.AddDays(-(((int)last.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
    }

    private static DateOnly EasterSunday(int year)
    {
        // Gregorian computus (Meeus/Jones/Butcher); Good Friday is two days earlier.
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
