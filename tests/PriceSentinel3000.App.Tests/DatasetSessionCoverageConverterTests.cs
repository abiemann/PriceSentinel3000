using System.Globalization;
using PriceSentinel3000.App.Converters;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.Tests;

public sealed class DatasetSessionCoverageConverterTests
{
    private static readonly DateOnly RegularDay = new(2026, 9, 8);
    private static readonly DateOnly EarlyCloseDay = new(2026, 11, 27);
    private readonly DatasetSessionCoverageConverter _converter = new();

    [Theory]
    [InlineData(15, "1,560")]
    [InlineData(30, "780")]
    [InlineData(60, "390")]
    [InlineData(120, "195")]
    public void FullRegularSessionUsesActualSourceInterval(int seconds, string expected)
    {
        HistoricalDatasetInfo dataset = Dataset(RegularDay, seconds: seconds);
        Assert.Equal("100%", Percent(dataset));
        Assert.Contains($"{expected} of {expected} {seconds}-second candles", Details(dataset));
        Assert.Contains("full regular session", Details(dataset));
    }

    [Fact]
    public void GapReducesFullSessionCoverage()
    {
        HistoricalDatasetInfo dataset = Dataset(RegularDay);
        DateTimeOffset from = dataset.Coverage.RequestedFromUtc;
        dataset = dataset with { Coverage = dataset.Coverage with
        {
            ActualCandleCount = 1440, Complete = false,
            Gaps = [new(from.AddHours(1), from.AddMinutes(90))],
        } };

        Assert.Equal("92.31%", Percent(dataset));
        Assert.Contains("1,440 of 1,560", Details(dataset));
    }

    [Fact]
    public void CompleteRequestedPrefixIsNotACompleteDay()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(RegularDay);
        HistoricalDatasetInfo dataset = Dataset(RegularDay, through: session.FromUtc.AddMinutes(30));

        Assert.True(dataset.Coverage.Complete);
        Assert.Equal(dataset.Coverage.ExpectedCandleCount, dataset.Coverage.ActualCandleCount);
        Assert.Equal("7.69%", Percent(dataset));
        Assert.Contains("120 of 1,560", Details(dataset));
        Assert.Contains("An open session stays below 100%", Details(dataset));
    }

    [Theory]
    [InlineData("regular", "840")]
    [InlineData("extended", "3,120")]
    public void EarlyCloseUsesShortenedSession(string bounds, string expected)
    {
        HistoricalDatasetInfo dataset = Dataset(EarlyCloseDay, bounds);
        Assert.Equal("100%", Percent(dataset));
        Assert.Contains($"{expected} of {expected}", Details(dataset));
        Assert.Contains($"full {bounds} session", Details(dataset));
    }

    [Fact]
    public void FullExtendedSessionUsesItsWholeDay()
    {
        HistoricalDatasetInfo dataset = Dataset(RegularDay, "extended");
        Assert.Equal("100%", Percent(dataset));
        Assert.Contains("3,840 of 3,840", Details(dataset));
    }

    [Theory]
    [InlineData("2026-09-08", "5,760")]
    [InlineData("2026-09-11", "4,800")]
    [InlineData("2026-09-13", "960")]
    [InlineData("2026-09-07", "960")]
    [InlineData("2026-11-27", "4,080")]
    public void TwentyFourFiveCountsOnlyActiveHoursForTheEasternDate(string day, string expected)
    {
        HistoricalDatasetInfo dataset = Dataset(DateOnly.Parse(day), "24_5");
        Assert.Equal("100%", Percent(dataset));
        Assert.Contains($"{expected} of {expected}", Details(dataset));
    }

    [Fact]
    public void ClosedHoursAfterEarlyCloseDoNotReduceTwentyFourFiveCoverage()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(EarlyCloseDay, "24_5");
        HistoricalDatasetInfo dataset = Dataset(EarlyCloseDay, "24_5", through: session.ThroughUtc.AddHours(7));
        dataset = dataset with { Coverage = dataset.Coverage with
        {
            ActualCandleCount = 4080, Complete = false, CoveredThroughUtc = session.ThroughUtc,
            Gaps = [new(session.ThroughUtc, session.ThroughUtc.AddHours(7))],
        } };

        Assert.Equal("100%", Percent(dataset));
        Assert.Contains("4,080 of 4,080", Details(dataset));
    }

    [Fact]
    public void SundayOvernightGapReducesOnlyTheFourHourSession()
    {
        HistoricalDatasetInfo dataset = Dataset(new(2026, 9, 13), "24_5");
        DateTimeOffset from = dataset.Coverage.RequestedFromUtc;
        dataset = dataset with { Coverage = dataset.Coverage with
        {
            ActualCandleCount = 720, Complete = false,
            Gaps = [new(from.AddHours(1), from.AddHours(2))],
        } };

        Assert.Equal("75%", Percent(dataset));
        Assert.Contains("720 of 960", Details(dataset));
    }

    [Fact]
    public void ImportedOutOfSessionCandlesDoNotInflateSessionCoverage()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(RegularDay);
        HistoricalDatasetInfo dataset = Dataset(RegularDay,
            from: session.FromUtc.AddHours(-1), through: session.FromUtc.AddMinutes(30));

        Assert.Equal(360, dataset.Coverage.ActualCandleCount);
        Assert.Equal("7.69%", Percent(dataset));
        Assert.Contains("120 of 1,560", Details(dataset));
    }

    [Fact]
    public void GapsOutsideSessionDoNotReduceSavedSessionCoverage()
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(RegularDay);
        HistoricalDatasetInfo dataset = Dataset(RegularDay, from: session.FromUtc.AddHours(-1));
        dataset = dataset with { Coverage = dataset.Coverage with
        {
            ActualCandleCount = 1560, Complete = false, CoveredFromUtc = session.FromUtc,
            Gaps = [new(session.FromUtc.AddHours(-1), session.FromUtc)],
        } };

        Assert.Equal("100%", Percent(dataset));
        Assert.Contains("1,560 of 1,560", Details(dataset));
    }

    [Fact]
    public void OneMissingCandleDoesNotRoundToComplete()
    {
        HistoricalDatasetInfo dataset = Dataset(RegularDay, "extended");
        DateTimeOffset through = dataset.Coverage.RequestedThroughUtc;
        dataset = dataset with { Coverage = dataset.Coverage with
        {
            ActualCandleCount = 3839, Complete = false, CoveredThroughUtc = through.AddSeconds(-15),
            Gaps = [new(through.AddSeconds(-15), through)],
        } };

        Assert.Equal("99.97%", Percent(dataset));
        Assert.Contains("3,839 of 3,840", Details(dataset));
    }

    [Theory]
    [InlineData("overnight", 15, 8)]
    [InlineData("regular", 15, 5)]
    [InlineData("regular", 5, 8)]
    [InlineData("24_5", 15, 5)]
    public void UnsupportedSessionDateOrIntervalHasNoInventedPercentage(string bounds, int seconds, int day)
    {
        HistoricalDatasetInfo dataset = Dataset(RegularDay) with
        {
            SessionBounds = bounds, SourceIntervalSeconds = seconds, TradingDate = new(2026, 9, day),
        };
        Assert.Equal("--", Percent(dataset));
        Assert.Contains("unavailable", Details(dataset));
    }

    private string Percent(HistoricalDatasetInfo dataset) =>
        (string)_converter.Convert(dataset, typeof(string), "", CultureInfo.GetCultureInfo("en-US"));

    private string Details(HistoricalDatasetInfo dataset) =>
        (string)_converter.Convert(dataset, typeof(string), "Details", CultureInfo.GetCultureInfo("en-US"));

    private static HistoricalDatasetInfo Dataset(DateOnly day, string bounds = "regular", int seconds = 15,
        DateTimeOffset? from = null, DateTimeOffset? through = null)
    {
        CollectionSessionWindow session = CollectionSchedule.GetSessionWindow(day, bounds);
        DateTimeOffset start = from ?? session.FromUtc, end = through ?? session.ThroughUtc;
        int candles = (int)((end - start).TotalSeconds / seconds);
        return new(new string('a', 64), "day.15s.json", "test", "instrument", "SOFI", day, seconds,
            "split", "robinhood-split-unversioned", bounds, session.ThroughUtc.AddHours(1),
            new(start, end, start, end, candles, candles, true, true, []));
    }
}
