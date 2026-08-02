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

    public static bool TryParseLocalRange(
        string? dateValue,
        string? startTimeValue,
        string? endTimeValue,
        out DateTimeOffset replayStart,
        out DateTimeOffset replayEnd)
    {
        replayEnd = default;

        if (!TryParseLocal(dateValue, startTimeValue, out replayStart) ||
            !TryParseLocal(dateValue, endTimeValue, out replayEnd))
        {
            return false;
        }

        if (replayEnd > replayStart)
        {
            return true;
        }

        if (!DateOnly.TryParseExact(
                dateValue?.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date))
        {
            return false;
        }

        return TryParseLocal(
            date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endTimeValue,
            out replayEnd);
    }

    public static bool TryCalculateEndTime(
        string? startTimeValue,
        int durationMinutes,
        out string endTimeValue)
    {
        endTimeValue = string.Empty;

        if (durationMinutes < 1 ||
            !TimeOnly.TryParseExact(
                startTimeValue?.Trim(),
                SupportedTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly startTime))
        {
            return false;
        }

        endTimeValue = startTime
            .AddMinutes(durationMinutes)
            .ToString("HH:mm", CultureInfo.InvariantCulture);
        return true;
    }
}
