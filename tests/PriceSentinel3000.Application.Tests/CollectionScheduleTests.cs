using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionScheduleTests
{
    [Theory]
    [InlineData("2026-09-08T20:14:59Z", "regular", "2026-09-04")]
    [InlineData("2026-09-08T20:15:00Z", "regular", "2026-09-08")]
    [InlineData("2026-09-07T23:00:00Z", "regular", "2026-09-04")]
    [InlineData("2026-11-27T18:15:00Z", "regular", "2026-11-27")]
    [InlineData("2026-11-27T22:14:59Z", "extended", "2026-11-25")]
    [InlineData("2026-11-27T22:15:00Z", "extended", "2026-11-27")]
    public void LatestFinalizedUsesCalendarCoverageAndDelay(string now, string bounds, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), CollectionSchedule.LatestFinalizedSession(DateTimeOffset.Parse(now), bounds, 15));

    [Theory]
    [InlineData("2026-03-08", "02:30", "2026-03-08T10:00:00Z")]
    [InlineData("2026-11-01", "01:30", "2026-11-01T08:30:00Z")]
    [InlineData("2026-09-08", "13:15", "2026-09-08T20:15:00Z")]
    [InlineData("2026-12-08", "13:15", "2026-12-08T21:15:00Z")]
    public void SavedZoneResolvesDstWithoutChangingClock(string day, string time, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), CollectionSchedule.ResolveDailyOccurrence(DateOnly.Parse(day),
            TimeOnly.Parse(time), "America/Los_Angeles"));

    [Fact]
    public void CatchUpIsBoundedAndNeverInventsPreEnableOccurrences()
    {
        var settings = new CollectionSettings { AutomaticDownloadsEnabled = true,
            AutomaticEnabledAtUtc = DateTimeOffset.Parse("2026-09-03T21:00:00Z"),
            TimeZoneId = "America/Los_Angeles", CatchUpCalendarDays = 7 };
        var due = CollectionSchedule.GetDueSessions(settings, null, DateTimeOffset.Parse("2026-09-09T20:15:00Z"));
        Assert.All(due, d => Assert.True(d.OccurrenceUtc >= settings.AutomaticEnabledAtUtc));
        Assert.DoesNotContain(due, d => d.SessionDate < new DateOnly(2026, 9, 4));
        Assert.Equal(new DateOnly(2026, 9, 9), due[^1].SessionDate);
        Assert.Empty(CollectionSchedule.GetDueSessions(settings, due[^1].OccurrenceUtc, due[^1].OccurrenceUtc));
    }

    [Fact]
    public void BeforeCloseChosenTimeUsesPreviousSessionAndRemainsUnchanged()
    {
        var settings = new CollectionSettings { AutomaticDownloadsEnabled = true,
            AutomaticEnabledAtUtc = DateTimeOffset.Parse("2026-09-08T07:00:00Z"),
            TimeZoneId = "America/Los_Angeles", DailyDownloadTime = new(12, 0) };
        var due = CollectionSchedule.GetDueSessions(settings, null, DateTimeOffset.Parse("2026-09-08T19:00:00Z"));
        Assert.Equal(new DateOnly(2026, 9, 4), Assert.Single(due).SessionDate);
        Assert.Equal(new TimeOnly(12, 0), settings.DailyDownloadTime);
    }
}
