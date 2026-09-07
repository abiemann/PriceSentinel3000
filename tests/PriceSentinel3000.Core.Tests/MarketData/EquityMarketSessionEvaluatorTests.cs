using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.Tests.MarketData;

public sealed class EquityMarketSessionEvaluatorTests
{
    [Theory]
    [InlineData("2026-08-31T13:29:59Z", false)]
    [InlineData("2026-08-31T13:30:00Z", true)]
    [InlineData("2026-08-31T19:59:59Z", true)]
    [InlineData("2026-08-31T20:00:00Z", false)]
    [InlineData("2026-09-05T16:00:00Z", false)]
    public void RegularHours_UseWeekdaysFromNineThirtyToFourEastern(
        string timestamp,
        bool expected)
    {
        bool actual = EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: false,
            isOvernightEligible: false);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("2026-08-31T07:59:59Z", false)]
    [InlineData("2026-08-31T08:00:00Z", true)]
    [InlineData("2026-08-31T23:59:59Z", true)]
    [InlineData("2026-09-01T00:00:00Z", false)]
    [InlineData("2026-09-05T16:00:00Z", false)]
    public void ExtendedHours_UseWeekdaysFromFourAmToEightPmEastern(
        string timestamp,
        bool expected)
    {
        bool actual = EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: true,
            isOvernightEligible: false);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("2026-08-30T23:59:59Z", false)]
    [InlineData("2026-08-31T00:00:00Z", true)]
    [InlineData("2026-09-02T07:00:00Z", true)]
    [InlineData("2026-09-04T23:59:59Z", true)]
    [InlineData("2026-09-05T00:00:00Z", false)]
    [InlineData("2026-09-05T16:00:00Z", false)]
    public void TwentyFourHourEligible_UsesSundayEightPmThroughFridayEightPmEastern(
        string timestamp,
        bool expected)
    {
        bool actual = EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: false,
            isOvernightEligible: true);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Eligibility_DistinguishesSchedulesOutsideRegularHours()
    {
        DateTimeOffset sundayEvening = DateTimeOffset.Parse("2026-08-31T00:30:00Z");

        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            sundayEvening,
            isExtendedHoursEligible: false,
            isOvernightEligible: false));
        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            sundayEvening,
            isExtendedHoursEligible: false,
            isOvernightEligible: true));
    }

    [Fact]
    public void OvernightEligibility_TakesPrecedenceOverExtendedHours()
    {
        DateTimeOffset mondayBeforeExtendedHours =
            DateTimeOffset.Parse("2026-08-31T05:00:00Z");

        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            mondayBeforeExtendedHours,
            isExtendedHoursEligible: true,
            isOvernightEligible: false));
        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            mondayBeforeExtendedHours,
            isExtendedHoursEligible: true,
            isOvernightEligible: true));
    }

    [Theory]
    [InlineData("2026-01-05T14:29:59Z", false)]
    [InlineData("2026-01-05T14:30:00Z", true)]
    public void NewYorkDaylightSavingRules_AreApplied(string timestamp, bool expected)
    {
        bool actual = EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: false,
            isOvernightEligible: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsTradableNow_UsesInjectedTimeProvider()
    {
        var evaluator = new EquityMarketSessionEvaluator(
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z")));

        Assert.True(evaluator.IsTradableNow(
            isExtendedHoursEligible: false,
            isOvernightEligible: true));
        Assert.False(evaluator.IsTradableNow(
            isExtendedHoursEligible: true,
            isOvernightEligible: false));
    }

    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("2026-01-19")]
    [InlineData("2026-02-16")]
    [InlineData("2026-04-03")]
    [InlineData("2026-05-25")]
    [InlineData("2026-06-19")]
    [InlineData("2026-07-03")]
    [InlineData("2026-09-07")]
    [InlineData("2026-11-26")]
    [InlineData("2026-12-25")]
    [InlineData("2027-01-01")]
    [InlineData("2027-01-18")]
    [InlineData("2027-02-15")]
    [InlineData("2027-03-26")]
    [InlineData("2027-05-31")]
    [InlineData("2027-06-18")]
    [InlineData("2027-07-05")]
    [InlineData("2027-09-06")]
    [InlineData("2027-11-25")]
    [InlineData("2027-12-24")]
    [InlineData("2028-01-17")]
    [InlineData("2028-02-21")]
    [InlineData("2028-04-14")]
    [InlineData("2028-05-29")]
    [InlineData("2028-06-19")]
    [InlineData("2028-07-04")]
    [InlineData("2028-09-04")]
    [InlineData("2028-11-23")]
    [InlineData("2028-12-25")]
    public void ExchangeHolidays_CloseRegularExtendedAndOvernightDaytime(string date)
    {
        DateTimeOffset daytime = DateTimeOffset.Parse($"{date}T16:00:00Z");

        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            daytime,
            isExtendedHoursEligible: false,
            isOvernightEligible: false));
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            daytime,
            isExtendedHoursEligible: true,
            isOvernightEligible: false));
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            daytime,
            isExtendedHoursEligible: true,
            isOvernightEligible: true));
    }

    [Theory]
    [InlineData("2026-09-06T20:00:00-04:00", false)]
    [InlineData("2026-09-06T20:22:00-07:00", false)]
    [InlineData("2026-09-07T00:00:00-04:00", false)]
    [InlineData("2026-09-07T19:59:59-04:00", false)]
    [InlineData("2026-09-07T20:00:00-04:00", true)]
    [InlineData("2026-09-08T00:00:00-04:00", true)]
    [InlineData("2026-07-02T19:59:59-04:00", true)]
    [InlineData("2026-07-02T20:00:00-04:00", false)]
    [InlineData("2026-11-26T19:59:59-05:00", false)]
    [InlineData("2026-11-26T20:00:00-05:00", true)]
    [InlineData("2026-11-27T00:00:00-05:00", true)]
    [InlineData("2026-12-23T20:00:00-05:00", true)]
    public void OvernightSession_BelongsToFollowingTradingDateAfterEightPm(
        string timestamp,
        bool expected)
    {
        Assert.Equal(expected, EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: false,
            isOvernightEligible: true));
    }

    [Theory]
    [InlineData("2026-09-07T20:00:00-04:00", false)]
    [InlineData("2026-09-08T09:29:59-04:00", false)]
    [InlineData("2026-09-08T09:30:00-04:00", true)]
    public void RegularSession_ReopensAtTuesdayOpeningAfterLaborDay(
        string timestamp,
        bool expected)
    {
        Assert.Equal(expected, EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: false,
            isOvernightEligible: false));
    }

    [Theory]
    [InlineData("2026-11-27", "-05:00")]
    [InlineData("2026-12-24", "-05:00")]
    [InlineData("2027-11-26", "-05:00")]
    [InlineData("2028-07-03", "-04:00")]
    [InlineData("2028-11-24", "-05:00")]
    public void HalfDays_CloseRegularAtOneAndExtendedAndOvernightAtFiveEastern(
        string date,
        string offset)
    {
        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T12:59:59{offset}"), false, false));
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T13:00:00{offset}"), false, false));

        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T16:59:59{offset}"), true, false));
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T17:00:00{offset}"), true, false));
        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T16:59:59{offset}"), false, true));
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T17:00:00{offset}"), false, true));
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T19:59:59{offset}"), false, true));
        // Each published half day in 2026–2028 precedes a holiday or weekend.
        Assert.False(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse($"{date}T20:00:00{offset}"), false, true));
    }

    [Theory]
    [InlineData("2026-07-02T15:59:59-04:00")]
    [InlineData("2027-12-31T15:59:59-05:00")]
    public void NonHolidayPrecedingObservedIndependenceDayOrSaturdayNewYear_RemainsFullDay(
        string timestamp)
    {
        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            DateTimeOffset.Parse(timestamp),
            isExtendedHoursEligible: false,
            isOvernightEligible: false));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
