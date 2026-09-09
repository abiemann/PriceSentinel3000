using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed class LibraryDaySummaryTests
{
    private static readonly DateOnly Day = new(2026, 9, 8);

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
        Assert.Contains("have not finished yet", summary.CoverageDetails);
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
            Dataset(day: Day.AddDays(-4)), original with { Symbol = "AMD" }]);
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

    private static LibraryDaySummary Single(params HistoricalDatasetInfo[] datasets) =>
        Assert.Single(LibraryDaySummary.Create(datasets));

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
