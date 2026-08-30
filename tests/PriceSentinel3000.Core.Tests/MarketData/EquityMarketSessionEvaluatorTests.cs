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

    [Fact]
    public void RegularSchedule_DoesNotApplyAnExchangeHolidayCalendar()
    {
        DateTimeOffset independenceDayObserved =
            DateTimeOffset.Parse("2026-07-03T14:00:00Z");

        Assert.True(EquityMarketSessionEvaluator.IsTradableAt(
            independenceDayObserved,
            isExtendedHoursEligible: false,
            isOvernightEligible: false));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
