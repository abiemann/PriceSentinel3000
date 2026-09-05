namespace PriceSentinel3000.Core.MarketData;

public static class EasternTradingDay
{
    private static readonly TimeZoneInfo EasternTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateOnly GetDate(DateTimeOffset timestampUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestampUtc, EasternTimeZone).DateTime);
}
