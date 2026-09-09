using System.IO;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed class LibraryCoverageTimelineTests
{
    private static readonly DateOnly Day = new(2026, 9, 9);
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Finished = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlocksDistinguishCompletePartialAndMissingNativeCandles()
    {
        DateTimeOffset from = Local(Day, 6);
        HistoricalDatasetInfo piece = Piece(from, from.AddMinutes(30));
        piece = piece with { Coverage = piece.Coverage with
        {
            ActualCandleCount = 90, Complete = false,
            Gaps = [new(from.AddMinutes(22.5), from.AddMinutes(30))],
        } };

        LibraryCoverageTimeline result = Create(false, [piece]);
        Assert.Equal(LibraryCoverageBlockState.Complete, result.Blocks[0].State);
        Assert.Equal(60, result.Blocks[0].SavedCandleCount);
        Assert.Equal(LibraryCoverageBlockState.Partial, result.Blocks[1].State);
        Assert.Equal(30, result.Blocks[1].SavedCandleCount);
        Assert.Equal(LibraryCoverageBlockState.Missing, result.Blocks[2].State);
        Assert.Equal(0, result.Blocks[2].SavedCandleCount);
        Assert.Equal(60, result.Blocks[2].ExpectedCandleCount);
        Assert.Contains("30 missing", result.Blocks[1].ToolTip);
    }

    [Fact]
    public void OverlappingCompatibleFilesCountEachCandleOnce()
    {
        DateTimeOffset from = Local(Day, 6);
        HistoricalDatasetInfo first = Piece(from, from.AddMinutes(10));
        HistoricalDatasetInfo second = Piece(from.AddMinutes(5), from.AddMinutes(15));

        LibraryCoverageTimeline result = Create(false, [first, second, first]);
        Assert.Equal(60, result.Blocks[0].SavedCandleCount);
        Assert.Equal(LibraryCoverageBlockState.Complete, result.Blocks[0].State);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("policy")]
    [InlineData("basis")]
    public void IncompatibleOverlappingSourcesAreRejectedInsteadOfShownAsComplete(string change)
    {
        DateTimeOffset from = Local(Day, 6);
        HistoricalDatasetInfo first = Piece(from, from.AddMinutes(15));
        HistoricalDatasetInfo second = change switch
        {
            "provider" => first with { Provider = "Another broker" },
            "instrument" => first with { InstrumentId = "Other listing" },
            "policy" => first with { AdjustmentPolicy = "none" },
            _ => first with { AdjustmentBasis = "another basis" },
        };
        Assert.Throws<InvalidDataException>(() => Create(false, [first, second]));
    }

    [Fact]
    public void CoveredBoundariesAndGapsDoNotInventMissingEdgesOrCountCoarseCandles()
    {
        DateTimeOffset from = Local(Day, 6);
        HistoricalDatasetInfo saved = Piece(from, from.AddMinutes(45));
        saved = saved with { Coverage = saved.Coverage with
        {
            CoveredFromUtc = from.AddMinutes(5), CoveredThroughUtc = from.AddMinutes(35),
            ActualCandleCount = 100, Complete = false,
            Gaps = [new(from.AddMinutes(20), from.AddMinutes(25))],
        } };
        HistoricalDatasetInfo coarse = Piece(from, from.AddMinutes(60)) with { SourceIntervalSeconds = 60 };
        HistoricalDatasetInfo other = Piece(from, from.AddMinutes(60)) with { Symbol = "MSFT" };

        LibraryCoverageTimeline result = Create(false, [saved, coarse, other]);
        Assert.Equal([40, 40, 20, 0], result.Blocks.Take(4).Select(block => block.SavedCandleCount).ToArray());
        Assert.All(result.Blocks.Take(3), block => Assert.Equal(LibraryCoverageBlockState.Partial, block.State));
    }

    [Fact]
    public void MisalignedNativeCoverageCannotProduceFalseCompleteBlocks()
    {
        DateTimeOffset from = Local(Day, 6);
        HistoricalDatasetInfo piece = Piece(from, from.AddMinutes(15));
        piece = piece with { Coverage = piece.Coverage with { CoveredFromUtc = from.AddSeconds(1) } };
        Assert.Throws<InvalidDataException>(() => Create(false, [piece]));
    }

    [Fact]
    public void TodayExcludesUnfinishedCandlesAndExplainsTheCurrentBlock()
    {
        DateTimeOffset from = Local(Day, 6);
        HistoricalDatasetInfo piece = Piece(from, from.AddMinutes(30));
        LibraryCoverageTimeline result = Create(false, [piece], from.AddMinutes(5).AddSeconds(7));

        LibraryCoverageBlock current = result.Blocks[0];
        Assert.Equal(20, current.ExpectedCandleCount);
        Assert.Equal(20, current.SavedCandleCount);
        Assert.Equal(LibraryCoverageBlockState.Complete, current.State);
        Assert.Contains("still in progress", current.ToolTip);
        Assert.Contains("future candles are excluded", current.ToolTip);
        Assert.Equal(LibraryCoverageBlockState.Future, result.Blocks[1].State);
        Assert.Equal(0, result.Blocks[1].ExpectedCandleCount);
        Assert.Equal(0, result.Blocks[1].SavedCandleCount);
        Assert.Contains("20 of 20", result.Summary);
    }

    [Fact]
    public void NonOvernightStocksUseSixToSeventeenLocalWithHourlyTicks()
    {
        LibraryCoverageTimeline result = Create(false, []);
        Assert.Equal(Local(Day, 6), result.FromUtc);
        Assert.Equal(Local(Day, 17), result.ThroughUtc);
        Assert.Equal(44, result.Blocks.Count);
        Assert.Equal(12, result.Ticks.Count);
        Assert.Equal("06:00", result.Ticks[0].Label);
        Assert.Equal("17:00", result.Ticks[^1].Label);
        Assert.Equal(0, result.Ticks[0].Position);
        Assert.Equal(1, result.Ticks[^1].Position);
        Assert.Contains("UTC-07:00", result.TimeZoneLabel);
        Assert.Contains("local time", result.RangeLabel);
    }

    [Fact]
    public void OvernightStocksUseTheEntireLocalDayWithTwoHourlyTicks()
    {
        LibraryCoverageTimeline result = Create(true, []);
        Assert.Equal(Local(Day, 0), result.FromUtc);
        Assert.Equal(Local(Day.AddDays(1), 0), result.ThroughUtc);
        Assert.Equal(96, result.Blocks.Count);
        Assert.Equal(13, result.Ticks.Count);
        Assert.Equal("00:00", result.Ticks[0].Label);
        Assert.Equal("24:00", result.Ticks[^1].Label);
        Assert.Null(result.Notice);
    }

    [Fact]
    public void UnknownEligibilityStaysFullDayUntilActualOvernightCoverageProvidesEvidence()
    {
        LibraryCoverageTimeline unknown = Create(null, []);
        Assert.Equal(96, unknown.Blocks.Count);
        Assert.Contains("eligibility is unknown", unknown.Notice);

        HistoricalDatasetInfo regularOnly = Piece(Local(Day, 6), Local(Day, 7)) with { SessionBounds = "24_5" };
        Assert.NotNull(Create(null, [regularOnly]).Notice);

        HistoricalDatasetInfo overnight = Piece(Local(Day, 21), Local(Day, 22));
        Assert.Null(Create(null, [overnight]).Notice);
        Assert.NotNull(Create(null, [overnight with { SourceIntervalSeconds = 60 }]).Notice);
    }

    [Fact]
    public void AdjacentEasternDailyFilesAreClippedAndCombinedIntoOnePacificCalendarDay()
    {
        DateTimeOffset easternMidnight = CollectionSchedule.ResolveDailyOccurrence(Day, TimeOnly.MinValue, Eastern.Id);
        HistoricalDatasetInfo first = Piece(easternMidnight, easternMidnight.AddDays(1));
        HistoricalDatasetInfo next = Piece(easternMidnight.AddDays(1), easternMidnight.AddDays(2));
        HistoricalDatasetInfo previous = Piece(easternMidnight.AddDays(-1), easternMidnight);

        LibraryCoverageTimeline result = Create(true, [previous, first, next]);
        Assert.Equal(Day, result.Date);
        Assert.Equal(5760, result.Blocks.Sum(block => block.SavedCandleCount));
        Assert.All(result.Blocks, block => Assert.Equal(LibraryCoverageBlockState.Complete, block.State));
        Assert.Equal(Local(Day.AddDays(1), 0), result.Blocks[^1].ThroughUtc);
        Assert.Equal(60, Create(true, [next]).Blocks[^1].SavedCandleCount);
    }

    [Fact]
    public void MarketClosuresAreSeparateFromMissingData()
    {
        DateOnly friday = new(2026, 9, 11);
        LibraryCoverageTimeline result = LibraryCoverageTimeline.Create(
            "AAPL", friday, Pacific, true, [], Finished.AddDays(3));
        Assert.Equal(LibraryCoverageBlockState.Missing, result.Blocks[67].State); // 16:45 Pacific.
        Assert.All(result.Blocks.Skip(68), block =>
        {
            Assert.Equal(LibraryCoverageBlockState.Closed, block.State); // Friday closes at 17:00 Pacific.
            Assert.Equal(0, block.ExpectedCandleCount);
        });

        DateOnly holiday = new(2026, 9, 7);
        LibraryCoverageTimeline closed = LibraryCoverageTimeline.Create("AAPL", holiday, Pacific, false, [], Finished);
        Assert.All(closed.Blocks, block => Assert.Equal(LibraryCoverageBlockState.Closed, block.State));
        Assert.Contains("No completed trading candles", closed.Summary);
    }

    [Theory]
    [InlineData(2026, 3, 8, 23, 92)]
    [InlineData(2026, 11, 1, 25, 100)]
    public void DaylightSavingChangesPreserveElapsedTimeAndLocalCalendarBoundaries(
        int year, int month, int day, int hours, int blockCount)
    {
        DateOnly date = new(year, month, day);
        LibraryCoverageTimeline result = LibraryCoverageTimeline.Create(
            "AAPL", date, Pacific, true, [], Local(date.AddDays(2), 0));
        Assert.Equal(hours, (result.ThroughUtc - result.FromUtc).TotalHours);
        Assert.Equal(blockCount, result.Blocks.Count);
        Assert.Equal(0, TimeZoneInfo.ConvertTime(result.FromUtc, Pacific).Hour);
        Assert.Equal(date.AddDays(1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(result.ThroughUtc, Pacific).DateTime));
        Assert.Equal("24:00", result.Ticks[^1].Label);
        Assert.Equal(1, result.Ticks[^1].Position);
        Assert.True(result.Ticks.Zip(result.Ticks.Skip(1)).All(pair => pair.First.Position < pair.Second.Position));
        Assert.All(result.Blocks, block => Assert.Equal(15, (block.ThroughUtc - block.FromUtc).TotalMinutes));
    }

    private static LibraryCoverageTimeline Create(
        bool? overnight, IEnumerable<HistoricalDatasetInfo> datasets, DateTimeOffset? now = null) =>
        LibraryCoverageTimeline.Create("AAPL", Day, Pacific, overnight, datasets, now ?? Finished);

    private static DateTimeOffset Local(DateOnly day, int hour) =>
        CollectionSchedule.ResolveDailyOccurrence(day, new(hour, 0), Pacific.Id);

    private static HistoricalDatasetInfo Piece(DateTimeOffset from, DateTimeOffset through)
    {
        int count = (int)((through - from).TotalSeconds / 15);
        return new("hash", "day.json", "Robinhood", "id-AAPL", "AAPL",
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, Eastern).DateTime), 15, "split",
            "robinhood-split-unversioned", "24_5", through.AddHours(1),
            new(from, through, from, through, count, count, true, true, []));
    }
}
