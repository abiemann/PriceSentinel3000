using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed class LibraryCoverageMissingRangeTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = Start.AddDays(1);

    [Fact]
    public void MissingSelectionIncludesConnectedBlocksOnBothSides()
    {
        LibraryCoverageTimeline timeline = Timeline(
            LibraryCoverageBlockState.Missing, LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Missing);

        var range = timeline.GetConnectedMissingRange(timeline.Blocks[1], Finished);

        Assert.Equal((Start, Start.AddMinutes(45)), range);
    }

    [Theory]
    [InlineData(LibraryCoverageBlockState.Complete)]
    [InlineData(LibraryCoverageBlockState.Partial)]
    [InlineData(LibraryCoverageBlockState.Closed)]
    [InlineData(LibraryCoverageBlockState.Future)]
    public void ConnectedRangeStopsAtEveryNonMissingState(LibraryCoverageBlockState boundary)
    {
        LibraryCoverageTimeline timeline = Timeline(
            LibraryCoverageBlockState.Missing, boundary, LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Missing, LibraryCoverageBlockState.Missing, boundary,
            LibraryCoverageBlockState.Missing);

        var range = timeline.GetConnectedMissingRange(timeline.Blocks[3], Finished);

        Assert.Equal((Start.AddMinutes(30), Start.AddMinutes(75)), range);
        Assert.Null(timeline.GetConnectedMissingRange(timeline.Blocks[1], Finished));
    }

    [Fact]
    public void MissingSpansSeparatedByUnrepresentedTimeAreNotConnected()
    {
        LibraryCoverageBlock[] blocks =
        [
            Missing(Start), Missing(Start.AddMinutes(30)), Missing(Start.AddMinutes(60)),
        ];
        LibraryCoverageTimeline timeline = Timeline(blocks);

        var range = timeline.GetConnectedMissingRange(blocks[1], Finished);

        Assert.Equal((Start.AddMinutes(30), Start.AddMinutes(45)), range);
    }

    [Fact]
    public void FormingBlockExcludesIncompleteCandleAndFutureBlocks()
    {
        LibraryCoverageTimeline timeline = Timeline(
            LibraryCoverageBlockState.Missing, LibraryCoverageBlockState.Missing,
            LibraryCoverageBlockState.Future);
        DateTimeOffset now = Start.AddMinutes(18).AddSeconds(52).ToOffset(TimeSpan.FromHours(-7));

        var range = timeline.GetConnectedMissingRange(timeline.Blocks[1], now);

        Assert.Equal((Start, Start.AddMinutes(18).AddSeconds(45)), range);
        Assert.Equal(TimeSpan.Zero, range!.Value.FromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, range.Value.ThroughUtc.Offset);
    }

    [Fact]
    public void SelectionMustExistAndHaveCompletedTime()
    {
        LibraryCoverageTimeline timeline = Timeline(LibraryCoverageBlockState.Missing);

        Assert.Null(timeline.GetConnectedMissingRange(null, Finished));
        Assert.Null(timeline.GetConnectedMissingRange(Missing(Start.AddHours(2)), Finished));
        Assert.Null(timeline.GetConnectedMissingRange(timeline.Blocks[0], Start.AddSeconds(14)));
        Assert.Equal((Start, Start.AddSeconds(15)),
            timeline.GetConnectedMissingRange(timeline.Blocks[0], Start.AddSeconds(15)));
    }

    [Fact]
    public void LocalDisplayMissingRangeStaysWithinSelectedEasternDailyFileBoundary()
    {
        TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        LibraryCoverageTimeline timeline = LibraryCoverageTimeline.Create(
            "AAPL", new DateOnly(2026, 9, 9), pacific, true, [],
            new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        LibraryCoverageBlock selected = Assert.Single(timeline.Blocks,
            block => block.FromUtc == new DateTimeOffset(2026, 9, 9, 7, 0, 0, TimeSpan.Zero));

        var range = timeline.GetConnectedMissingRange(selected, Finished.AddDays(1));

        Assert.Equal((timeline.FromUtc, timeline.ThroughUtc), range);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 4, 0, 0, TimeSpan.Zero), range!.Value.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero), range.Value.ThroughUtc);
    }

    private static LibraryCoverageBlock Missing(DateTimeOffset from) =>
        new(from, from.AddMinutes(15), 0, 60, LibraryCoverageBlockState.Missing, "Missing");

    private static LibraryCoverageTimeline Timeline(params LibraryCoverageBlockState[] states) =>
        Timeline(states.Select((state, index) => Missing(Start.AddMinutes(15 * index)) with { State = state }).ToArray());

    private static LibraryCoverageTimeline Timeline(LibraryCoverageBlock[] blocks) =>
        new("AAPL", new(2026, 9, 9), "UTC", "", "", null,
            blocks[0].FromUtc, blocks[^1].ThroughUtc, blocks, []);
}
