using System.Globalization;

namespace PriceSentinel3000.Core.Configuration;

public static class ReplaySchedule
{
    private static readonly string[] SupportedTimeFormats = ["H:mm", "HH:mm"];

    public static bool TryParseLocal(
        string? dateValue,
        string? timeValue,
        out DateTimeOffset replayStart)
    {
        replayStart = default;

        if (!DateOnly.TryParseExact(
                dateValue?.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date) ||
            !TimeOnly.TryParseExact(
                timeValue?.Trim(),
                SupportedTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly time))
        {
            return false;
        }

        DateTime localTime = DateTime.SpecifyKind(
            date.ToDateTime(time),
            DateTimeKind.Unspecified);
        TimeZoneInfo localZone = TimeZoneInfo.Local;

        if (localZone.IsInvalidTime(localTime))
        {
            return false;
        }

        replayStart = new DateTimeOffset(
            localTime,
            localZone.GetUtcOffset(localTime));
        return true;
    }
}
