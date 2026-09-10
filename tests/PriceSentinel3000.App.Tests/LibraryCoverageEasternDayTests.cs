using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed class LibraryCoverageEasternDayTests
{
    private static readonly DateOnly Day = new(2026, 9, 10);
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 23, 38, 7, TimeSpan.FromHours(-7));

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void EasternDayAlreadyInProgressBeforePacificMidnightShowsSavedCoverage(bool? overnight)
    {
        LibraryCoverageTimeline timeline = LibraryCoverageTimeline.Create(
            "SOFI", Day, Pacific, overnight, [Piece(Start, Start.AddSeconds(35 * 15))], Now);

        Assert.Equal(Day, timeline.Date);
        Assert.Equal(Start, timeline.FromUtc);
        Assert.Equal(Start.AddDays(1), timeline.ThroughUtc);
        Assert.Contains("2026-09-09 21:00", timeline.RangeLabel);
        Assert.Contains("2026-09-10 21:00", timeline.RangeLabel);
        Assert.Equal(35, timeline.Blocks.Sum(block => block.SavedCandleCount));
        Assert.Equal(632, timeline.Blocks.Sum(block => block.ExpectedCandleCount));
        Assert.Contains("35 of 632", timeline.Summary);
        Assert.Equal(LibraryCoverageBlockState.Partial, timeline.Blocks[0].State);
        Assert.Equal(35, timeline.Blocks[0].SavedCandleCount);
        Assert.All(timeline.Blocks.Skip(1).Take(10), block =>
            Assert.Equal(LibraryCoverageBlockState.Missing, block.State));
        Assert.Equal(32, timeline.Blocks[10].ExpectedCandleCount);
        Assert.Contains("still in progress", timeline.Blocks[10].ToolTip);
        Assert.All(timeline.Blocks.Skip(11), block =>
            Assert.Equal(LibraryCoverageBlockState.Future, block.State));
    }

    [Fact]
    public void DisplayTimezoneDoesNotChangeSelectedEasternDayCoverage()
    {
        HistoricalDatasetInfo[] pieces =
        [
            Piece(Start.AddDays(-1), Start),
            Piece(Start, Start.AddSeconds(35 * 15)),
            Piece(Start.AddDays(1), Start.AddDays(2)),
        ];
        LibraryCoverageTimeline pacific = LibraryCoverageTimeline.Create("SOFI", Day, Pacific, true, pieces, Now);

        foreach (TimeZoneInfo timezone in new[] { Eastern, TimeZoneInfo.Utc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo") })
        {
            LibraryCoverageTimeline other = LibraryCoverageTimeline.Create("SOFI", Day, timezone, true, pieces, Now);

            Assert.Equal(pacific.FromUtc, other.FromUtc);
            Assert.Equal(pacific.ThroughUtc, other.ThroughUtc);
            Assert.Equal(pacific.Summary, other.Summary);
            Assert.Equal(pacific.Blocks.Select(block =>
                (block.FromUtc, block.ThroughUtc, block.SavedCandleCount, block.ExpectedCandleCount, block.State)),
                other.Blocks.Select(block =>
                (block.FromUtc, block.ThroughUtc, block.SavedCandleCount, block.ExpectedCandleCount, block.State)));
        }
        Assert.Equal(35, pacific.Blocks.Sum(block => block.SavedCandleCount));
    }

    [Fact]
    public void MissingDownloadStaysWithinEasternDayAndCompletedCandleCutoff()
    {
        LibraryCoverageTimeline timeline = LibraryCoverageTimeline.Create(
            "SOFI", Day, Pacific, true, [Piece(Start, Start.AddSeconds(35 * 15))], Now);

        var range = timeline.GetConnectedMissingRange(timeline.Blocks[1], Now);

        Assert.NotNull(range);
        Assert.Equal(Start.AddMinutes(15), range.Value.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 6, 38, 0, TimeSpan.Zero), range.Value.ThroughUtc);
        Assert.Equal(Day, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(range.Value.FromUtc, Eastern).DateTime));
        Assert.Equal(Day, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(range.Value.ThroughUtc, Eastern).DateTime));
        Assert.Null(timeline.GetConnectedMissingRange(timeline.Blocks[11], Now));

        LibraryCoverageTimeline elapsed = LibraryCoverageTimeline.Create(
            "SOFI", Day, Pacific, true, [], Start.AddDays(2));
        Assert.Equal((Start, Start.AddDays(1)),
            elapsed.GetConnectedMissingRange(elapsed.Blocks[0], Start.AddDays(2)));
    }

    [Theory]
    [InlineData(2026, 3, 8, 5, 4, 92)]
    [InlineData(2026, 11, 1, 4, 5, 100)]
    public void EasternDaylightSavingBoundariesRemainFixedAcrossDisplayTimezones(
        int year, int month, int day, int startUtcHour, int endUtcHour, int blockCount)
    {
        DateOnly date = new(year, month, day);
        DateTimeOffset from = new(year, month, day, startUtcHour, 0, 0, TimeSpan.Zero);
        DateTimeOffset through = new DateTimeOffset(year, month, day, endUtcHour, 0, 0, TimeSpan.Zero).AddDays(1);

        foreach (TimeZoneInfo timezone in new[] { Pacific, Eastern, TimeZoneInfo.Utc })
        {
            LibraryCoverageTimeline timeline = LibraryCoverageTimeline.Create(
                "SOFI", date, timezone, true, [], through.AddDays(1));

            Assert.Equal(from, timeline.FromUtc);
            Assert.Equal(through, timeline.ThroughUtc);
            Assert.Equal(blockCount, timeline.Blocks.Count);
            Assert.Equal(from, timeline.Blocks[0].FromUtc);
            Assert.Equal(through, timeline.Blocks[^1].ThroughUtc);
        }
    }

    private static HistoricalDatasetInfo Piece(DateTimeOffset from, DateTimeOffset through)
    {
        int count = (int)((through - from).TotalSeconds / 15);
        return new("hash", "day.json", "Robinhood", "id-SOFI", "SOFI",
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, Eastern).DateTime), 15, "split",
            "robinhood-split-unversioned", "24_5", through.AddHours(1),
            new(from, through, from, through, count, count, true, true, []));
    }
}
