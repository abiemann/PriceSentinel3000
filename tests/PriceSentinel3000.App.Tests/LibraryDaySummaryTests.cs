using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed class LibraryDaySummaryTests
{
    private static readonly DateOnly Day = new(2026, 9, 8);
    private static readonly DateTimeOffset Finished = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GrowingRevisionsCountEachSavedCandleOnlyOnce()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        LibraryDaySummary summary = Single(
            Dataset(through: session.FromUtc.AddMinutes(30)),
            Dataset(through: session.FromUtc.AddMinutes(60)),
            Dataset(through: session.FromUtc.AddMinutes(45)));

        Assert.Equal(240L, summary.CandleCount);
        Assert.Equal(100m * 240 / 1560, summary.CoveragePercent);
        Assert.Contains("3 saved files", summary.CoverageDetails);
        Assert.Contains("count once", summary.CoverageDetails);
        Assert.Contains("Replay checks", summary.CoverageDetails);
    }

    [Fact]
    public void ComplementaryFilesFillGapsWithoutSummingOverlaps()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        LibraryDaySummary summary = Single(
            Dataset(gaps: [new(session.FromUtc.AddHours(1), session.FromUtc.AddHours(2))]),
            Dataset(gaps: [new(session.FromUtc.AddHours(3), session.FromUtc.AddHours(4))]));

        Assert.Equal(1560L, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
    }

    [Fact]
    public void UnfilledGapIsStillMissingFromTheDailyUnion()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        HistoricalGap gap = new(session.FromUtc.AddHours(1), session.FromUtc.AddMinutes(90));
        LibraryDaySummary summary = Single(Dataset(gaps: [gap]),
            Dataset(from: session.FromUtc.AddHours(2), through: session.FromUtc.AddHours(3)));

        Assert.Equal(1440L, summary.CandleCount);
        Assert.Equal(100m * 1440 / 1560, summary.CoveragePercent);
    }

    [Fact]
    public void RegularAndAllHoursFilesUseOneAllHoursDenominator()
    {
        CollectionSessionWindow regular = CollectionSchedule.GetSessionWindow(Day);
        CollectionSessionWindow allHours = CollectionSchedule.GetSessionWindow(Day, "24_5");
        LibraryDaySummary summary = Single(
            Dataset(through: regular.FromUtc.AddSeconds(1240 * 15)),
            Dataset(through: regular.FromUtc.AddSeconds(1333 * 15)),
            Dataset(through: regular.FromUtc.AddSeconds(1497 * 15)),
            Dataset(bounds: "24_5", through: allHours.FromUtc.AddSeconds(758 * 15)),
            Dataset(bounds: "24_5", from: allHours.FromUtc.AddHours(6),
                through: allHours.FromUtc.AddHours(6).AddSeconds(444 * 15)),
            Dataset(bounds: "24_5", from: regular.ThroughUtc,
                through: regular.ThroughUtc.AddSeconds(376 * 15)));

        Assert.Equal(3075L, summary.CandleCount);
        Assert.Equal(100m * 3075 / 5760, summary.CoveragePercent);
        Assert.Contains("all-hours", summary.CoverageDetails);
        Assert.Contains("6 saved files", summary.CoverageDetails);
    }

    [Fact]
    public void RegularAndExtendedUseTheExtendedDay()
    {
        LibraryDaySummary summary = Single(Dataset(), Dataset(bounds: "extended"));
        Assert.Equal(3840L, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
        Assert.Contains("full extended day", summary.CoverageDetails);
    }

    [Fact]
    public void CompleteRequestedPrefixDoesNotClaimACompleteDay()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        LibraryDaySummary summary = Single(Dataset(through: session.FromUtc.AddMinutes(30)));
        Assert.Equal(120L, summary.CandleCount);
        Assert.Equal(100m * 120 / 1560, summary.CoveragePercent);
        Assert.Contains("full regular day", summary.CoverageDetails);
        Assert.Contains("future candles are excluded", summary.CoverageDetails);
    }

    [Theory]
    [InlineData("regular", 840)]
    [InlineData("extended", 3120)]
    [InlineData("24_5", 4080)]
    public void EarlyCloseUsesOnlyItsTradingHours(string bounds, long expected)
    {
        LibraryDaySummary summary = Single(Dataset(day: new(2026, 11, 27), bounds: bounds));
        Assert.Equal(expected, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
    }

    [Fact]
    public void ClosedPeriodsDoNotInflateCoverageOrItsDenominator()
    {
        DateOnly earlyClose = new(2026, 11, 27);
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(earlyClose, "24_5");
        // Imported candles outside the scheduled session do not enlarge daily coverage.
        LibraryDaySummary summary = Single(Dataset(day: earlyClose, bounds: "24_5",
            through: session.ThroughUtc.AddHours(7)));
        Assert.Equal(4080L, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
    }

    [Theory]
    [InlineData("2026-09-11", 4800)]
    [InlineData("2026-09-13", 960)]
    [InlineData("2026-09-07", 960)]
    public void OvernightCoverageUsesActiveHoursOnItsEasternDate(string day, long expected)
    {
        LibraryDaySummary summary = Single(Dataset(day: DateOnly.Parse(day), bounds: "24_5"));
        Assert.Equal(expected, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
    }

    [Fact]
    public void DifferentStocksAndDatesStaySeparateAndSorted()
    {
        HistoricalDatasetInfo original = Dataset();
        IReadOnlyList<LibraryDaySummary> summaries = LibraryDaySummary.Create([
            original with { Symbol = "MSFT" }, original,
            Dataset(day: Day.AddDays(-4)), original with { Symbol = "AMD" }], Finished);
        Assert.Equal(new[] { "AAPL", "AMD", "MSFT", "AAPL" }, summaries.Select(item => item.Symbol));
        Assert.Equal(new[] { Day, Day, Day, Day.AddDays(-4) }, summaries.Select(item => item.TradingDate));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("instrument")]
    [InlineData("policy")]
    [InlineData("basis")]
    public void IncompatibleSourcesRemainOneRowWithoutCombinedCounts(string changed)
    {
        HistoricalDatasetInfo dataset = Dataset();
        HistoricalDatasetInfo other = changed switch
        {
            "provider" => dataset with { Provider = "another provider" },
            "instrument" => dataset with { InstrumentId = "another instrument" },
            "policy" => dataset with { AdjustmentPolicy = "raw" },
            _ => dataset with { AdjustmentBasis = "another adjustment basis" },
        };
        LibraryDaySummary summary = Single(dataset, other);
        Assert.Null(summary.CandleCount);
        Assert.Null(summary.CoveragePercent);
        Assert.Contains("coverage is not combined", summary.CoverageDetails);
        Assert.Equal(changed == "provider" ? "Mixed" : "Robinhood", summary.Provider);
    }

    [Fact]
    public void DifferentNativeIntervalsAreNeverCountedAsFifteenSecondCandles()
    {
        LibraryDaySummary summary = Single(Dataset(), Dataset(seconds: 60));
        Assert.Equal("Mixed", summary.SourceIntervalDisplay);
        Assert.Null(summary.CandleCount);
        Assert.Null(summary.CoveragePercent);
        Assert.Contains("coarser candles are not counted as finer", summary.CoverageDetails);
    }

    [Fact]
    public void CoarseOnlyFilesRetainTheirActualInterval()
    {
        LibraryDaySummary summary = Single(Dataset(seconds: 120), Dataset(seconds: 120));
        Assert.Equal("120", summary.SourceIntervalDisplay);
        Assert.Equal(195L, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
    }

    [Theory]
    [InlineData("overnight", 15, 8)]
    [InlineData("regular", 5, 8)]
    [InlineData("regular", 15, 5)]
    public void UnsupportedCoverageDoesNotInventAPercentage(string bounds, int seconds, int day)
    {
        LibraryDaySummary summary = Single(Dataset() with
        {
            SessionBounds = bounds, SourceIntervalSeconds = seconds, TradingDate = new(2026, 9, day),
        });
        Assert.Null(summary.CandleCount);
        Assert.Null(summary.CoveragePercent);
        Assert.Contains("unavailable", summary.CoverageDetails);
    }

    [Fact]
    public void ShiftedNativeGridIsReportedInsteadOfInferringCandleCounts()
    {
        HistoricalDatasetInfo dataset = Dataset();
        LibraryDaySummary summary = Single(dataset, dataset with { Coverage = dataset.Coverage with
        {
            CoveredFromUtc = dataset.Coverage.CoveredFromUtc!.Value.AddSeconds(5),
        } });
        Assert.Null(summary.CandleCount);
        Assert.Null(summary.CoveragePercent);
        Assert.Contains("native interval grid", summary.CoverageDetails);
    }

    [Fact]
    public void EmptyValidatedFileContributesNoCoverage()
    {
        HistoricalDatasetInfo dataset = Dataset();
        LibraryDaySummary summary = Single(dataset with { Coverage = dataset.Coverage with
        {
            ActualCandleCount = 0, CoveredFromUtc = null, CoveredThroughUtc = null,
            Complete = false, Gaps = [new(dataset.Coverage.RequestedFromUtc, dataset.Coverage.RequestedThroughUtc)],
        } });
        Assert.Equal(0L, summary.CandleCount);
        Assert.Equal(0m, summary.CoveragePercent);
    }

    [Theory]
    [InlineData("regular", 600)]
    [InlineData("extended", 1920)]
    [InlineData("24_5", 2880)]
    public void NoonIsCompleteWhenEveryCompletedCandleIsSaved(string bounds, long expected)
    {
        DateTimeOffset noon = new(2026, 9, 8, 16, 0, 0, TimeSpan.Zero);
        LibraryDaySummary summary = Assert.Single(LibraryDaySummary.Create(
            [Dataset(bounds: bounds, through: noon)], noon));

        Assert.Equal(expected, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
        Assert.Contains("12:00:00 Eastern", summary.CoverageDetails);
        Assert.Contains("Eastern calendar date 2026-09-08", summary.CoverageDetails);
        Assert.Contains("market closures and future candles are excluded", summary.CoverageDetails);
    }

    [Fact]
    public void SavedAndExpectedCoverageStopAtTheSameCompletedCutoffWhileStoredCountIsPreserved()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        DateTimeOffset noon = session.FromUtc.AddHours(2.5);
        HistoricalDatasetInfo dataset = Dataset(gaps:
        [
            new(session.FromUtc, session.FromUtc.AddMinutes(30)),
            new(noon.AddHours(1), noon.AddHours(2)),
        ]);
        LibraryDaySummary summary = Assert.Single(LibraryDaySummary.Create([dataset], noon));

        Assert.Equal(1200L, summary.CandleCount);
        Assert.Equal(80m, summary.CoveragePercent);
        Assert.Contains("480 of 600", summary.CoverageDetails);
        Assert.Contains("1,200 stored candles", summary.CoverageDetails);
    }

    [Fact]
    public void SavedCandlesAfterNowDoNotInflateCoverageAndTheUnfinishedCandleIsExcluded()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        HistoricalDatasetInfo completeFile = Dataset();
        DateTimeOffset oneCandleAndFourteenSeconds = session.FromUtc.AddSeconds(29);
        LibraryDaySummary summary = Assert.Single(LibraryDaySummary.Create(
            [completeFile], oneCandleAndFourteenSeconds));

        Assert.Equal(1560L, summary.CandleCount);
        Assert.Equal(100m, summary.CoveragePercent);
        Assert.Contains("1 of 1", summary.CoverageDetails);
        Assert.Contains("09:30:15 Eastern", summary.CoverageDetails);
        Assert.Equal(summary, Assert.Single(LibraryDaySummary.Create([completeFile], session.FromUtc.AddSeconds(15))));
        Assert.Contains("2 of 2", Assert.Single(LibraryDaySummary.Create(
            [completeFile], session.FromUtc.AddSeconds(30))).CoverageDetails);
    }

    [Fact]
    public void MissingCompletedPrefixHasZeroCoverageEvenWhenLaterCandlesAreStored()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        HistoricalDatasetInfo later = Dataset(from: session.FromUtc.AddHours(1));
        LibraryDaySummary summary = Assert.Single(LibraryDaySummary.Create(
            [later], session.FromUtc.AddMinutes(30)));

        Assert.Equal(1320L, summary.CandleCount);
        Assert.Equal(0m, summary.CoveragePercent);
        Assert.Contains("0 of 120", summary.CoverageDetails);
    }

    [Theory]
    [InlineData(-86400)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(14)]
    public void BeforeAnyCompletedCandleCoverageIsUnknownAndStoredCountRemainsVisible(int secondsFromOpen)
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(Day);
        LibraryDaySummary summary = Assert.Single(LibraryDaySummary.Create(
            [Dataset()], session.FromUtc.AddSeconds(secondsFromOpen)));

        Assert.Equal(1560L, summary.CandleCount);
        Assert.Null(summary.CoveragePercent);
        Assert.Contains("No completed 15-second candles are expected", summary.CoverageDetails);
    }

    [Fact]
    public void HolidayEveningIsExcludedUntilItsFirstCandleCompletes()
    {
        DateOnly holiday = new(2026, 9, 7);
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(holiday, "24_5");
        HistoricalDatasetInfo dataset = Dataset(day: holiday, bounds: "24_5");
        LibraryDaySummary before = Assert.Single(LibraryDaySummary.Create([dataset], session.FromUtc.AddSeconds(14)));
        LibraryDaySummary after = Assert.Single(LibraryDaySummary.Create([dataset], session.FromUtc.AddMinutes(30)));

        Assert.Null(before.CoveragePercent);
        Assert.Equal(960L, before.CandleCount);
        Assert.Equal(100m, after.CoveragePercent);
        Assert.Contains("120 of 120", after.CoverageDetails);
        Assert.Contains("20:30:00 Eastern", after.CoverageDetails);
    }

    [Fact]
    public void PastDaysIncludingEarlyCloseRemainEqualAsTheClockAdvances()
    {
        HistoricalDatasetInfo[] datasets =
        [
            Dataset(), Dataset(day: new(2026, 11, 27), bounds: "24_5"),
            Dataset(day: new(2026, 9, 7), bounds: "24_5"),
        ];
        Assert.Equal(LibraryDaySummary.Create(datasets, Finished),
            LibraryDaySummary.Create(datasets, Finished.AddDays(5)));
    }
    private static LibraryDaySummary Single(params HistoricalDatasetInfo[] datasets) =>
        Assert.Single(LibraryDaySummary.Create(datasets, Finished));

    private static HistoricalDatasetInfo Dataset(DateOnly? day = null, string bounds = "regular", int seconds = 15,
        DateTimeOffset? from = null, DateTimeOffset? through = null, HistoricalGap[]? gaps = null)
    {
        DateOnly date = day ?? Day;
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(date, bounds);
        DateTimeOffset start = from ?? session.FromUtc, end = through ?? session.ThroughUtc;
        gaps ??= [];
        int expected = (int)((end - start).TotalSeconds / seconds);
        int actual = expected - (int)(gaps.Sum(gap => (gap.ThroughUtc - gap.FromUtc).TotalSeconds) / seconds);
        return new(Guid.NewGuid().ToString("N").PadRight(64, '0'), "day.json", "Robinhood", "instrument", "AAPL",
            date, seconds, "split", "robinhood-split-unversioned", bounds, session.ThroughUtc.AddDays(1),
            new(start, end, start, end, expected, actual, gaps.Length == 0, true, gaps));
    }
}
