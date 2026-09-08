using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed class CollectionBackfillPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T20:30:00Z");
    private static readonly DateOnly Latest = new(2026, 9, 9);

    [Fact]
    public void FirstCollectionBootstrapsOnlyRecentTradingSessions()
    {
        CollectionBackfillPlan plan = Plan([]);
        Assert.Equal(Days("2026-09-03", "2026-09-04", "2026-09-08", "2026-09-09"), plan.MissingSessions);
        Assert.Empty(plan.ExpiredGaps);
    }

    [Fact]
    public void EachTickerResumesFromItsOwnSavedHistory()
    {
        HistoricalDatasetInfo[] files = [File("2026-09-04"), File("2026-09-08", symbol: "SOFI")];
        Assert.Equal(Days("2026-09-08", "2026-09-09"), Plan(files).MissingSessions);
        var sofi = CollectionBackfillPlanner.Plan(new("SOFI"), files, Latest, "regular", Now);
        Assert.Equal(Days("2026-09-09"), sofi.MissingSessions);
    }

    [Fact]
    public void EarlierPartialAndDeletedInteriorDaysRemainMissingAfterLaterCompleteFiles()
    {
        HistoricalDatasetInfo[] files = [File("2026-09-02"), File("2026-09-04", partial: true), File("2026-09-08")];
        CollectionBackfillPlan plan = Plan(files);
        Assert.Equal(Days("2026-09-03", "2026-09-04", "2026-09-09"), plan.MissingSessions);
        Assert.Empty(plan.ExpiredGaps);
    }

    [Fact]
    public void ImportedFilesAreSufficientWithoutCollectionJobsOrOriginalPaths()
    {
        HistoricalDatasetInfo copied = File("2026-09-04") with { RelativePath = "2026/09 - September/NFLX/copied.json" };
        CollectionBackfillPlan plan = Plan([copied]);
        Assert.Equal(Days("2026-09-08", "2026-09-09"), plan.MissingSessions);
        Assert.Empty(plan.ExpiredGaps);
    }

    [Fact]
    public void OldGapsAreCompactAndDoNotDisplaceRecentDownloadWork()
    {
        HistoricalDatasetInfo[] files = [File("2026-08-24"), File("2026-08-25", partial: true),
            File("2026-08-27"), File("2026-09-04")];
        CollectionBackfillPlan plan = Plan(files);
        Assert.Equal(Days("2026-09-03", "2026-09-08", "2026-09-09"), plan.MissingSessions);
        Assert.Equal(new[] { Gap("2026-08-25", "2026-08-26"), Gap("2026-08-28", "2026-09-02") }, plan.ExpiredGaps);
    }

    [Fact]
    public void YearsOfflineProduceOneOldGapAndOnlyRecentSessionJobs()
    {
        CollectionBackfillPlan plan = Plan([File("2000-01-03")]);
        Assert.Equal(Gap("2000-01-04", "2026-09-02"), Assert.Single(plan.ExpiredGaps));
        Assert.Equal(4, plan.MissingSessions.Count);
    }

    [Fact]
    public void KnownTrackingStartPreservesLeadingMissingFiles()
    {
        CollectionBackfillPlan plan = CollectionBackfillPlanner.Plan(new("NFLX"), [File("2026-09-08")],
            Latest, "regular", Now, trackingFromSessionDate: new(2026, 8, 24));
        Assert.Equal(Gap("2026-08-24", "2026-09-02"), Assert.Single(plan.ExpiredGaps));
        Assert.Equal(Days("2026-09-03", "2026-09-04", "2026-09-09"), plan.MissingSessions);
    }

    [Fact]
    public void KnownTrackingStartSurvivesAllFilesBeingDeleted()
    {
        CollectionBackfillPlan plan = CollectionBackfillPlanner.Plan(new("NFLX"), [],
            Latest, "regular", Now, trackingFromSessionDate: new(2026, 8, 24));
        Assert.Equal(Gap("2026-08-24", "2026-09-02"), Assert.Single(plan.ExpiredGaps));
        Assert.Equal(4, plan.MissingSessions.Count);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void CoarseFilesNeverSatisfy15SecondContinuity(int interval)
    {
        CollectionBackfillPlan plan = Plan([File("2026-09-04", interval: interval)]);
        Assert.Equal(Days("2026-09-04", "2026-09-08", "2026-09-09"), plan.MissingSessions);
    }

    [Fact]
    public void CompleteShortRequestedRangeIsNotACompleteSession()
    {
        HistoricalDatasetInfo file = File("2026-09-04");
        DateTimeOffset laterStart = file.Coverage.RequestedFromUtc.AddHours(1);
        file = file with { Coverage = file.Coverage with { RequestedFromUtc = laterStart, CoveredFromUtc = laterStart } };
        Assert.Contains(new DateOnly(2026, 9, 4), Plan([file]).MissingSessions);
    }

    [Fact]
    public void UnknownVolumeDoesNotCauseRepeatedPriceDownloads()
    {
        HistoricalDatasetInfo file = File("2026-09-04");
        file = file with { Coverage = file.Coverage with { HasCompleteVolume = false } };
        Assert.DoesNotContain(file.TradingDate, Plan([file]).MissingSessions);
    }

    [Fact]
    public void NewerPartialRevisionIsRetriedEvenWhenOlderRevisionWasComplete()
    {
        HistoricalDatasetInfo old = File("2026-09-04");
        HistoricalDatasetInfo latest = File("2026-09-04", partial: true) with { FetchedAtUtc = old.FetchedAtUtc.AddHours(1) };
        Assert.Contains(old.TradingDate, Plan([old, latest]).MissingSessions);
        Assert.DoesNotContain(old.TradingDate, Plan([old with { FetchedAtUtc = latest.FetchedAtUtc.AddHours(1) }, latest]).MissingSessions);
    }

    [Fact]
    public void EqualTimeRevisionsUseTheSameHashTieBreakAsLibraryQueries()
    {
        HistoricalDatasetInfo complete = File("2026-09-04") with { DatasetHash = "b" };
        HistoricalDatasetInfo partial = File("2026-09-04", partial: true) with { DatasetHash = "a" };
        Assert.Contains(complete.TradingDate, Plan([complete, partial]).MissingSessions);
    }

    [Fact]
    public void OtherSessionBoundsAndAdjustmentIdentityDoNotCountAsCoverage()
    {
        HistoricalDatasetInfo[] files = [File("2026-09-03", bounds: "extended"),
            File("2026-09-04") with { AdjustmentBasis = "a different revision" },
            File("2026-09-08") with { AdjustmentPolicy = "raw" }];
        Assert.Equal(4, Plan(files).MissingSessions.Count);
    }

    [Fact]
    public void KnownInstrumentFiltersReusedSymbolsAndUnknownInstrumentDoesNotMergeSources()
    {
        HistoricalDatasetInfo oldInstrument = File("2026-09-04") with { InstrumentId = "old" };
        HistoricalDatasetInfo current = File("2026-09-04") with { InstrumentId = "current" };
        var known = CollectionBackfillPlanner.Plan(new("NFLX", ProviderInstrumentId: "current"),
            [oldInstrument], Latest, "regular", Now);
        Assert.Equal(4, known.MissingSessions.Count);
        Assert.Contains(current.TradingDate, Plan([oldInstrument, current]).MissingSessions);
    }

    [Fact]
    public void RetryBoundaryUsesExactSessionCloseRatherThanMidnight()
    {
        var justInRange = CollectionBackfillPlanner.Plan(new("NFLX"), [], Latest, "regular",
            DateTimeOffset.Parse("2026-09-09T20:00:00Z"));
        var justExpired = CollectionBackfillPlanner.Plan(new("NFLX"), [], Latest, "regular",
            DateTimeOffset.Parse("2026-09-09T20:00:01Z"));
        Assert.Equal(new DateOnly(2026, 9, 2), justInRange.MissingSessions[0]);
        Assert.Equal(new DateOnly(2026, 9, 3), justExpired.MissingSessions[0]);
    }

    [Fact]
    public void EarlyCloseAndThanksgivingUseTradingCalendar()
    {
        HistoricalDatasetInfo[] files = [File("2026-11-23"), File("2026-11-24"), File("2026-11-25"), File("2026-11-27")];
        var plan = CollectionBackfillPlanner.Plan(new("NFLX"), files, new(2026, 11, 27), "regular",
            DateTimeOffset.Parse("2026-11-27T18:30:00Z"));
        Assert.Empty(plan.MissingSessions);
        Assert.Empty(plan.ExpiredGaps);
        Assert.Equal(18, files[^1].Coverage.CoveredThroughUtc!.Value.UtcDateTime.Hour);
    }

    [Fact]
    public void FutureFilesDoNotMoveTrackingStartPastLatestFinalizedSession()
    {
        Assert.Equal(4, Plan([File("2026-09-10")]).MissingSessions.Count);
    }

    private static CollectionBackfillPlan Plan(IReadOnlyList<HistoricalDatasetInfo> files) =>
        CollectionBackfillPlanner.Plan(new("NFLX"), files, Latest, "regular", Now);

    private static DateOnly[] Days(params string[] dates) => dates.Select(DateOnly.Parse).ToArray();
    private static CollectionBackfillGap Gap(string from, string through) => new(DateOnly.Parse(from), DateOnly.Parse(through));

    private static HistoricalDatasetInfo File(string date, string symbol = "NFLX", bool partial = false,
        int interval = 15, string bounds = "regular")
    {
        DateOnly day = DateOnly.Parse(date);
        CollectionSessionWindow window = CollectionSchedule.GetSessionWindow(day, bounds);
        int count = (int)(window.ThroughUtc - window.FromUtc).TotalSeconds / interval;
        var gaps = partial ? new[] { new HistoricalGap(window.FromUtc, window.FromUtc.AddSeconds(interval)) } : [];
        var coverage = new HistoricalCoverage(window.FromUtc, window.ThroughUtc,
            partial ? window.FromUtc.AddSeconds(interval) : window.FromUtc, window.ThroughUtc,
            count, partial ? count - 1 : count, !partial, true, gaps);
        return new($"{symbol}-{date}-{interval}", $"{symbol}/{date}.json", "Robinhood", $"id-{symbol}", symbol, day,
            interval, "split", "robinhood-split-unversioned", bounds, window.ThroughUtc.AddHours(1), coverage);
    }
}
