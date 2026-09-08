using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class AllHoursBackfillPlannerTests
{
    private static readonly DateOnly Day = new(2026, 9, 9);
    private static readonly CollectionSessionWindow Window = CollectionSchedule.GetSessionWindow(Day, "24_5");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisjointNativeFragmentsEstablishCompleteRecentAndExpiredDays(bool expired)
    {
        CollectionBackfillPlan plan = Plan([Piece(0, 12), Piece(12, 24)], expired);

        Assert.Empty(plan.MissingSessions);
        Assert.Empty(plan.ExpiredGaps);
    }

    [Fact]
    public void MissingIntervalBetweenFragmentsRemainsAContinuityGap()
    {
        HistoricalDatasetInfo second = Piece(12, 24);
        DateTimeOffset gapEnd = second.Coverage.RequestedFromUtc.AddSeconds(15);
        second = second with { Coverage = second.Coverage with
        {
            RequestedFromUtc = gapEnd, CoveredFromUtc = gapEnd,
            ExpectedCandleCount = second.Coverage.ExpectedCandleCount - 1,
            ActualCandleCount = second.Coverage.ActualCandleCount - 1,
        } };

        Assert.Equal(Day, Assert.Single(Plan([Piece(0, 12), second]).MissingSessions));
    }

    [Fact]
    public void NewerPartialCorrectionReplacesOnlyItsExactRequestedWindow()
    {
        HistoricalDatasetInfo original = Piece(0, 12);
        HistoricalDatasetInfo correction = original with
        {
            DatasetHash = "corrected", FetchedAtUtc = original.FetchedAtUtc.AddHours(1),
            Coverage = original.Coverage with
            {
                Complete = false, ActualCandleCount = original.Coverage.ActualCandleCount - 1,
                Gaps = [new(Window.FromUtc.AddHours(6), Window.FromUtc.AddHours(6).AddSeconds(15))],
            },
        };

        Assert.Equal(Day, Assert.Single(Plan([original, correction, Piece(12, 24)]).MissingSessions));
        Assert.Empty(Plan([original with { FetchedAtUtc = correction.FetchedAtUtc.AddHours(1) }, correction, Piece(12, 24)]).MissingSessions);
    }

    [Fact]
    public void OverlappingFragmentCountsCannotHideAnUncoveredTail()
    {
        HistoricalDatasetInfo[] overlap = [Piece(0, 18), Piece(6, 18), Piece(6, 18)];
        Assert.True(overlap.Sum(d => d.Coverage.ActualCandleCount) > 24 * 60 * 4);

        Assert.Equal(Day, Assert.Single(Plan(overlap).MissingSessions));
        Assert.Empty(Plan([.. overlap, Piece(18, 24)]).MissingSessions);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("instrument")]
    public void FragmentUnionStillRequiresOneSourceIdentity(string field)
    {
        HistoricalDatasetInfo second = Piece(12, 24);
        second = field == "provider" ? second with { Provider = "other" } : second with { InstrumentId = "other" };

        Assert.Equal(Day, Assert.Single(Plan([Piece(0, 12), second]).MissingSessions));
    }

    [Fact]
    public void SundayEveningRequiresOnlyItsActiveSessionWindow()
    {
        DateOnly sunday = new(2026, 9, 13);
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(sunday, "24_5");
        HistoricalDatasetInfo complete = Piece(0, 4) with
        {
            TradingDate = sunday, FetchedAtUtc = window.ThroughUtc.AddHours(1),
            Coverage = new(window.FromUtc, window.ThroughUtc, window.FromUtc, window.ThroughUtc, 960, 960, true, true, []),
        };

        CollectionBackfillPlan plan = CollectionBackfillPlanner.Plan(new("NFLX"), [complete], sunday,
            "24_5", window.ThroughUtc.AddHours(1));

        Assert.Empty(plan.MissingSessions);
        Assert.Empty(plan.ExpiredGaps);
    }

    private static CollectionBackfillPlan Plan(HistoricalDatasetInfo[] pieces, bool expired = false) =>
        CollectionBackfillPlanner.Plan(new("NFLX"), pieces, Day, "24_5", Window.ThroughUtc.AddHours(1).AddDays(expired ? 8 : 0));

    private static HistoricalDatasetInfo Piece(int fromHour, int throughHour)
    {
        DateTimeOffset from = Window.FromUtc.AddHours(fromHour), through = Window.FromUtc.AddHours(throughHour);
        int count = (throughHour - fromHour) * 60 * 4;
        return new($"piece-{fromHour}-{throughHour}", "fragment.json", "Robinhood", "id-NFLX", "NFLX", Day,
            15, "split", "robinhood-split-unversioned", "24_5", Window.ThroughUtc.AddHours(1),
            new(from, through, from, through, count, count, true, true, []));
    }
}
