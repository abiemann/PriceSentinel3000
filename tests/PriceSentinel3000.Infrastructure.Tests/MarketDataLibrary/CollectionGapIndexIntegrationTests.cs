using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.Tests.MarketDataLibrary;

public sealed class CollectionGapIndexIntegrationTests
{
    private static readonly DateOnly Today = new(2026, 9, 16);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task RepeatedDownloadAfterRestartSkipsConfirmedOlderEmptyRanges()
    {
        using var fixture = new Fixture();
        fixture.Provider.Candles.Add(Candle(Open(Today)));
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        int attempts = fixture.Provider.Requests.Count;
        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Yesterday);
        Assert.NotEmpty(fixture.Snapshot(Yesterday).UnavailableRanges);

        fixture.Restart();
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(attempts, fixture.Provider.Requests.Count);
        Assert.Equal(CollectionJobStatus.Unavailable,
            Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday).Status);
        Assert.Single(fixture.Read(Today).Candles);
    }

    [Fact]
    public async Task TodaysEmptyRangeIsEligibleAgainAtFifteenMinutes()
    {
        using var fixture = new Fixture();
        DateTimeOffset through = fixture.Clock.Now;
        fixture.StartOnly(Today, through);
        await fixture.Drain();
        HistoricalDataRequest original = Assert.Single(fixture.Provider.Requests);

        fixture.Clock.Now = through.AddMinutes(15).AddSeconds(-1);
        fixture.StartOnly(Today, through);
        await fixture.Drain();
        Assert.Single(fixture.Provider.Requests);

        fixture.Clock.Now = through.AddMinutes(15);
        fixture.StartOnly(Today, through);
        await fixture.Drain();

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(original, fixture.Provider.Requests[1]);
    }

    [Fact]
    public async Task TodaysTemporaryObservationStillExpiresAfterEasternMidnight()
    {
        using var fixture = new Fixture();
        DateTimeOffset checkedAt = Open(Today).AddHours(23).AddMinutes(59).AddSeconds(30);
        fixture.Clock.Now = checkedAt;
        DateTimeOffset gapStart = checkedAt.AddSeconds(-15);
        fixture.RememberEmpty(Today, Open(Today), gapStart, checkedAt.AddMinutes(30));
        fixture.StartOnly(Today, checkedAt);
        await fixture.Drain();
        HistoricalDataRequest original = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(gapStart, original.FromUtc);

        fixture.Clock.Now = checkedAt.AddMinutes(5);
        fixture.StartOnly(Today, checkedAt);
        await fixture.Drain();
        Assert.Single(fixture.Provider.Requests);

        fixture.Clock.Now = checkedAt.AddMinutes(15);
        fixture.StartOnly(Today, checkedAt);
        await fixture.Drain();

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(original, fixture.Provider.Requests[1]);
        Assert.Equal(Today.AddDays(1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fixture.Clock.Now, Eastern).DateTime));
    }

    [Fact]
    public async Task CurrentDayRequestCompletedAfterMidnightKeepsFifteenMinuteRetry()
    {
        using var fixture = new Fixture();
        DateTimeOffset requestedAt = Open(Today).AddHours(23).AddMinutes(59).AddSeconds(30);
        fixture.Clock.Now = requestedAt;
        fixture.RememberEmpty(Today, Open(Today), requestedAt.AddSeconds(-15), requestedAt.AddHours(1));
        fixture.StartOnly(Today, requestedAt);
        DateTimeOffset respondedAt = requestedAt.AddMinutes(2);
        fixture.Provider.BeforeResponse = () => fixture.Clock.Now = respondedAt;

        await fixture.Drain();

        HistoricalDataRequest original = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(Today.AddDays(1), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(respondedAt, Eastern).DateTime));
        fixture.Provider.BeforeResponse = null;
        fixture.Clock.Now = respondedAt.AddMinutes(15).AddSeconds(-1);
        fixture.StartOnly(Today, requestedAt);
        await fixture.Drain();
        Assert.Single(fixture.Provider.Requests);

        fixture.Clock.Now = respondedAt.AddMinutes(15);
        fixture.StartOnly(Today, requestedAt);
        await fixture.Drain();

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(original, fixture.Provider.Requests[1]);
    }

    [Fact]
    public async Task GroupedRequestsNeverBridgeEvenAShortRememberedEmptyBlock()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday);
        fixture.RememberEmpty(Yesterday, start.AddMinutes(1), start.AddMinutes(2));
        fixture.StartOnly(Yesterday, start.AddMinutes(4));

        await fixture.Drain();

        Assert.Collection(fixture.Provider.Requests,
            request => AssertRange(request, start, start.AddMinutes(1)),
            request => AssertRange(request, start.AddMinutes(2), start.AddMinutes(4)));
        Assert.Equal(new HistoricalGap(start, start.AddMinutes(4)),
            Assert.Single(fixture.Snapshot(Yesterday, start.AddMinutes(4)).UnavailableRanges));
    }

    [Fact]
    public async Task CachedRangesAreClippedAtBothEdgesOfTheRequestedWindow()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday);
        fixture.RememberEmpty(Yesterday, start, start.AddSeconds(30));
        fixture.RememberEmpty(Yesterday, start.AddMinutes(2), start.AddMinutes(3));
        fixture.StartOnly(Yesterday, start.AddMinutes(2).AddSeconds(30));

        await fixture.Drain();

        AssertRange(Assert.Single(fixture.Provider.Requests), start.AddSeconds(30), start.AddMinutes(2));
    }

    [Fact]
    public async Task CachedGapsOnAPreviouslyProductiveDayDoNotHideEarlierUnattemptedData()
    {
        using var fixture = new Fixture();
        fixture.Seed(Today, [Candle(Open(Today))]);
        HistoricalCandle yesterday = Candle(Open(Yesterday));
        fixture.Seed(Yesterday, [yesterday]);
        fixture.RememberEmpty(Yesterday, yesterday.EndsAtUtc, Close(Yesterday));
        fixture.Index.RecordAttempt(Key(Yesterday), yesterday.StartsAtUtc, yesterday.EndsAtUtc,
            [], true, fixture.Clock.Now, null);
        HistoricalCandle earlier = Candle(Open(Yesterday.AddDays(-1)).AddHours(9));
        fixture.Provider.Candles.Add(earlier);

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.DoesNotContain(fixture.Provider.Requests, request => Day(request) == Yesterday);
        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Yesterday.AddDays(-1));
        Assert.Equal(earlier, Assert.Single(fixture.Read(Yesterday.AddDays(-1)).Candles));
        Assert.Equal(yesterday, Assert.Single(fixture.Read(Yesterday).Candles));
    }

    [Fact]
    public async Task FreshEmptyChecksStillStopAnOlderDayDespiteHistoricalPositiveEvidence()
    {
        using var fixture = new Fixture();
        fixture.Seed(Today, [Candle(Open(Today))]);
        HistoricalCandle archived = Candle(Open(Yesterday));
        fixture.Seed(Yesterday, [archived]);
        fixture.Index.RecordAttempt(Key(Yesterday), archived.StartsAtUtc, archived.EndsAtUtc,
            [], true, fixture.Clock.Now, null);

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Yesterday);
        Assert.DoesNotContain(fixture.Provider.Requests, request => Day(request) < Yesterday);
        Assert.Equal(archived, Assert.Single(fixture.Read(Yesterday).Candles));
    }

    [Fact]
    public async Task MissingDatabaseIsRecreatedAndRelearnedWithoutChangingSavedCandles()
    {
        using var fixture = new Fixture();
        fixture.Provider.Candles.Add(Candle(Open(Today)));
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        HistoricalDataQueryResult before = fixture.Read(Today);
        Assert.Single(before.Candles);
        int attempts = fixture.Provider.Requests.Count;
        Assert.True(File.Exists(fixture.DatabasePath));
        File.Delete(fixture.DatabasePath);
        Assert.False(File.Exists(fixture.DatabasePath));

        await fixture.Collector.QueueAvailableAsync();
        Assert.True(File.Exists(fixture.DatabasePath));
        await fixture.Drain();

        Assert.True(fixture.Provider.Requests.Count > attempts);
        Assert.All(fixture.Provider.Requests.Skip(attempts), request => Assert.Equal(Yesterday, Day(request)));
        Assert.Equal(before.Candles, fixture.Read(Today).Candles);
        Assert.Equal(before.Datasets.Select(dataset => dataset.DatasetHash),
            fixture.Read(Today).Datasets.Select(dataset => dataset.DatasetHash));
        Assert.NotEmpty(fixture.Snapshot(Yesterday).UnavailableRanges);
    }

    [Fact]
    public async Task PartialBatchedResponsesKeepOmittedGapsUnconfirmedAndFinishEachRunOnce()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday), through = start.AddMinutes(2);
        HistoricalCandle returned = Candle(start.AddSeconds(30));
        fixture.Provider.Candles.Add(returned);
        fixture.StartOnly(Yesterday, through);

        await fixture.Drain();

        CollectionGapSnapshot snapshot = fixture.Snapshot(Yesterday, through);
        Assert.True(snapshot.HasReturnedCandles);
        Assert.Empty(snapshot.UnavailableRanges);
        Assert.Equal(returned, Assert.Single(fixture.Read(Yesterday).Candles));
        Assert.NotEmpty(fixture.Read(Yesterday).Coverage.Gaps);
        AssertRange(Assert.Single(fixture.Provider.Requests), start, through);
        string savedHash = Assert.Single(fixture.Read(Yesterday).Datasets).DatasetHash;

        fixture.Restart();
        fixture.StartOnly(Yesterday, through);
        await fixture.Drain();

        // Both holes share one batch; the repeated saved candle is verified and discarded.
        Assert.Equal(2, fixture.Provider.Requests.Count);
        AssertRange(fixture.Provider.Requests[1], start, through);
        Assert.Equal(savedHash, Assert.Single(fixture.Read(Yesterday).Datasets).DatasetHash);
        Assert.Equal(returned, Assert.Single(fixture.Read(Yesterday).Candles));
        Assert.Empty(fixture.Snapshot(Yesterday, through).UnavailableRanges);
        fixture.Restart();
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Equal(2, fixture.Provider.Requests.Count);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("connection")]
    [InlineData("provenance")]
    public async Task FailedOrInvalidResponsesNeverCreateUnavailableEvidence(string failure)
    {
        using var fixture = new Fixture();
        DateTimeOffset through = Open(Yesterday).AddSeconds(15);
        fixture.Provider.Failure = failure;
        fixture.StartOnly(Yesterday, through);

        await fixture.Collector.TickAsync(true);

        CollectionGapSnapshot snapshot = fixture.Snapshot(Yesterday, through);
        Assert.Empty(snapshot.UnavailableRanges);
        Assert.False(snapshot.HasReturnedCandles);
        Assert.Single(fixture.Provider.Requests);
        fixture.Provider.Failure = null;
        fixture.StartOnly(Yesterday, through);
        await fixture.Drain();
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Snapshot(Yesterday, through).UnavailableRanges);
    }

    [Fact]
    public async Task CancellationDuringTheBrokerRequestDoesNotRecordAnEmptyResult()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        DateTimeOffset through = Open(Yesterday).AddSeconds(15);
        fixture.Provider.BeforeResponse = cancellation.Cancel;
        fixture.StartOnly(Yesterday, through);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector.TickAsync(true, cancellation.Token));

        Assert.Empty(fixture.Snapshot(Yesterday, through).UnavailableRanges);
        Assert.Single(fixture.Provider.Requests);
        fixture.Provider.BeforeResponse = null;
        fixture.StartOnly(Yesterday, through);
        await fixture.Drain();
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Snapshot(Yesterday, through).UnavailableRanges);
    }

    [Fact]
    public async Task ForcedRunRechecksRememberedOlderAndTodaysEmptyRangesThenNormalRunSkipsThemAgain()
    {
        using var fixture = new Fixture();
        DateTimeOffset firstCheckedAt = fixture.Clock.Now;
        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        int originalAttempts = fixture.Provider.Requests.Count;
        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Today);
        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Yesterday);

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Equal(originalAttempts, fixture.Provider.Requests.Count);

        fixture.Clock.Now = firstCheckedAt.AddMinutes(5);
        DateTimeOffset forcedThrough = fixture.Clock.Now;
        await fixture.Collector.QueueForcedAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] forcedRequests = fixture.Provider.Requests.Skip(originalAttempts).ToArray();
        Assert.Contains(forcedRequests, request => Day(request) == Today && request.FromUtc == Open(Today));
        Assert.Contains(forcedRequests, request => Day(request) == Yesterday);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.True(job.IgnoreKnownGaps));
        int forcedAttempts = fixture.Provider.Requests.Count;

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(forcedAttempts, fixture.Provider.Requests.Count);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.False(job.IgnoreKnownGaps));
        // The forced empty response refreshes today's observation rather than merely bypassing it.
        fixture.Clock.Now = firstCheckedAt.AddMinutes(16);
        Assert.Equal(new HistoricalGap(Open(Today), forcedThrough),
            Assert.Single(fixture.Snapshot(Today, forcedThrough).UnavailableRanges));
    }

    [Fact]
    public async Task ForcedRunBatchesNearbyGapsAndPreservesPreviouslySavedCandles()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Today), through = start.AddMinutes(1);
        fixture.Clock.Now = through;
        HistoricalCandle original = Candle(start.AddSeconds(15));
        fixture.Seed(Today, [original]);
        fixture.RememberEmpty(Today, start, original.StartsAtUtc, through.AddMinutes(15));
        fixture.RememberEmpty(Today, original.EndsAtUtc, through, through.AddMinutes(15));
        fixture.RememberEmpty(Yesterday, Open(Yesterday), Close(Yesterday));
        HistoricalCandle[] additions = [Candle(start), Candle(start.AddSeconds(30)), Candle(start.AddSeconds(45))];
        fixture.Provider.Candles.AddRange(additions.Append(original));

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();
        Assert.Empty(fixture.Provider.Requests);

        await fixture.Collector.QueueForcedAvailableAsync();
        await fixture.Drain();

        HistoricalDataRequest[] todayRequests = fixture.Provider.Requests.Where(request => Day(request) == Today).ToArray();
        AssertRange(Assert.Single(todayRequests), start, through);
        Assert.Equal(additions.Append(original).OrderBy(candle => candle.StartsAtUtc), fixture.Read(Today).Candles);
        Assert.Equal(original, Assert.Single(fixture.Read(Today).Candles, candle => candle.StartsAtUtc == original.StartsAtUtc));
        Assert.Single(fixture.Library.Scan().Datasets, dataset => dataset.TradingDate == Today);
        Assert.Empty(fixture.Snapshot(Today, through).UnavailableRanges);
        Assert.True(fixture.Snapshot(Today, through).HasReturnedCandles);
        int attempts = fixture.Provider.Requests.Count;

        await fixture.Collector.QueueAvailableAsync();
        await fixture.Drain();

        Assert.Equal(attempts, fixture.Provider.Requests.Count);
        Assert.Equal(additions.Append(original).OrderBy(candle => candle.StartsAtUtc), fixture.Read(Today).Candles);
    }

    [Theory]
    [InlineData(CollectionJobStatus.Failed)]
    [InlineData(CollectionJobStatus.Pending)]
    public async Task ForcedRunResetsRetryBudgetsAndPreservesBypassThroughRestartAndOlderDiscovery(CollectionJobStatus previousStatus)
    {
        using var fixture = new Fixture();
        fixture.RememberEmpty(Today, Open(Today), fixture.Clock.Now, fixture.Clock.Now.AddMinutes(15));
        fixture.RememberEmpty(Yesterday, Open(Yesterday), Close(Yesterday));
        fixture.StartOnly(Today, fixture.Clock.Now);
        CollectionJob previous = Assert.Single(fixture.Store.Load().Jobs) with
        {
            Status = previousStatus, Attempts = 99, RetryAfterUtc = fixture.Clock.Now.AddHours(1),
            LastAttemptAtUtc = fixture.Clock.Now.AddMinutes(-1), NextGapFromUtc = fixture.Clock.Now,
            ReceivedCandlesThisRun = true, Error = "Previous retry allowance exhausted.",
            IsAvailabilityProbe = true, DiscoveryAsOfDate = Today,
        };
        fixture.Store.Save(fixture.Store.Load() with { Jobs = [previous] });
        fixture.Restart();

        await fixture.Collector.QueueForcedAvailableAsync();

        CollectionJob queued = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Pending, queued.Status);
        Assert.Equal(0, queued.Attempts);
        Assert.Null(queued.RetryAfterUtc);
        Assert.Null(queued.LastAttemptAtUtc);
        Assert.Null(queued.NextGapFromUtc);
        Assert.Null(queued.Error);
        Assert.False(queued.ReceivedCandlesThisRun);
        Assert.True(queued.IgnoreKnownGaps);

        fixture.Restart();
        Assert.True(Assert.Single(fixture.Collector.State.Jobs).IgnoreKnownGaps);
        await fixture.Collector.TickAsync(true);
        Assert.Equal(Today, Day(Assert.Single(fixture.Provider.Requests)));
        await fixture.Collector.TickAsync(true);
        CollectionJob older = Assert.Single(fixture.Collector.State.Jobs, job => job.SessionDate == Yesterday);
        Assert.Equal(CollectionJobStatus.Pending, older.Status);
        Assert.True(older.IgnoreKnownGaps);

        fixture.Restart();
        await fixture.Collector.TickAsync(true);
        Assert.Equal(Yesterday, Day(fixture.Provider.Requests.Last()));
        int attempts = fixture.Provider.Requests.Count;

        await fixture.Collector.QueueAvailableAsync();
        Assert.All(fixture.Collector.State.Jobs.Where(job => job.Status == CollectionJobStatus.Pending),
            job => Assert.False(job.IgnoreKnownGaps));
        await fixture.Drain();
        Assert.Equal(attempts, fixture.Provider.Requests.Count);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.False(job.IgnoreKnownGaps));
    }

    [Fact]
    public async Task ForcedRunStillHonorsTheTransientAttemptLimit()
    {
        using var fixture = new Fixture();
        fixture.RememberEmpty(Today, Open(Today), fixture.Clock.Now, fixture.Clock.Now.AddMinutes(15));
        fixture.Provider.Failure = "http";

        await fixture.Collector.QueueForcedAvailableAsync();
        await fixture.Collector.TickAsync(true);

        CollectionJob failed = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.True(failed.IgnoreKnownGaps);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Single(fixture.Provider.Requests);

        await fixture.Collector.QueueForcedAvailableAsync();
        Assert.Equal(0, Assert.Single(fixture.Collector.State.Jobs).Attempts);
        await fixture.Collector.TickAsync(true);

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(CollectionJobStatus.Failed, Assert.Single(fixture.Collector.State.Jobs).Status);
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Equal(2, fixture.Provider.Requests.Count);
    }

    [Fact]
    public async Task DueScheduleDefersUntilForcedRunFinishesThenUsesTheGapIndexNormally()
    {
        using var fixture = new Fixture();
        fixture.RememberEmpty(Today, Open(Today), fixture.Clock.Now, fixture.Clock.Now.AddMinutes(15));
        fixture.RememberEmpty(Yesterday, Open(Yesterday), Close(Yesterday));
        CollectionState before = fixture.Store.Load();
        fixture.Store.Save(before with { Settings = before.Settings with
        {
            AutomaticDownloadsEnabled = true, AutomaticEnabledAtUtc = Open(Today).AddMinutes(-1),
            DailyDownloadTime = TimeOnly.MinValue, TimeZoneId = Eastern.Id,
        } });
        fixture.Restart();
        await fixture.Collector.QueueForcedAvailableAsync();

        await fixture.Collector.TickAsync(true);

        Assert.Equal(Today, Day(Assert.Single(fixture.Provider.Requests)));
        Assert.True(Assert.Single(fixture.Collector.State.Jobs).IgnoreKnownGaps);
        Assert.Null(fixture.Collector.State.LastScheduledOccurrenceUtc);
        await fixture.Drain();
        Assert.Contains(fixture.Provider.Requests, request => Day(request) == Yesterday);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.True(job.IgnoreKnownGaps));
        Assert.Null(fixture.Collector.State.LastScheduledOccurrenceUtc);
        int forcedAttempts = fixture.Provider.Requests.Count;

        // The overdue occurrence can now start an ordinary scheduled run.
        await fixture.Collector.TickAsync(true);
        Assert.Equal(Open(Today), fixture.Collector.State.LastScheduledOccurrenceUtc);
        await fixture.Drain();

        Assert.Equal(forcedAttempts, fixture.Provider.Requests.Count);
        Assert.All(fixture.Collector.State.Jobs, job => Assert.False(job.IgnoreKnownGaps));
    }

    [Fact]
    public async Task HundredsOfFragmentedHolesUseOneBatchAndPreserveDuplicatesAcrossRestart()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday), through = start.AddHours(1);
        HistoricalCandle[] original = Enumerable.Range(0, 120)
            .Select(index => Candle(start.AddSeconds(index * 30 + 15))).ToArray();
        fixture.Seed(Yesterday, original);
        string originalHash = Assert.Single(fixture.Read(Yesterday).Datasets).DatasetHash;
        HistoricalCandle[] additions = [Candle(start), Candle(start.AddSeconds(30))];
        fixture.Provider.Candles.AddRange(original.Concat(additions));
        fixture.StartOnly(Yesterday, through);

        await fixture.Collector.TickAsync(true);

        HistoricalDataRequest batch = Assert.Single(fixture.Provider.Requests);
        AssertRange(batch, start, through.AddSeconds(-15));
        Assert.Equal(batch.ThroughUtc, Assert.Single(fixture.Collector.State.Jobs).NextGapFromUtc);
        Assert.Equal(original.Concat(additions).OrderBy(candle => candle.StartsAtUtc), fixture.Read(Yesterday).Candles);
        Assert.Equal(original, fixture.Library.Read(originalHash).Candles);
        Assert.Empty(fixture.Snapshot(Yesterday, through).UnavailableRanges);

        fixture.Restart();
        await fixture.Drain();

        Assert.Single(fixture.Provider.Requests);
        Assert.Single(fixture.Library.Scan().Datasets, dataset => dataset.TradingDate == Yesterday);
        Assert.Equal(CollectionBatchResult.Idle, await fixture.Collector.TickAsync(true));
        Assert.Single(fixture.Provider.Requests);
    }

    [Theory]
    [InlineData(false, 6, 2)]
    [InlineData(true, 1, 7)]
    public async Task BatchesRespectTheRequestSpanAndCollectionCutoff(bool availabilityCheck, int maximumHours, int expectedRequests)
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday), through = start.AddHours(6.5);
        HistoricalCandle original = Candle(start.AddHours(3));
        fixture.Seed(Yesterday, [original]);
        string originalHash = Assert.Single(fixture.Read(Yesterday).Datasets).DatasetHash;
        fixture.StartOnly(Yesterday, through, availabilityCheck);

        await fixture.Drain();

        Assert.Equal(expectedRequests, fixture.Provider.Requests.Count);
        Assert.All(fixture.Provider.Requests, request =>
        {
            Assert.True(request.ThroughUtc - request.FromUtc <= TimeSpan.FromHours(maximumHours));
            Assert.True(request.FromUtc >= start && request.ThroughUtc <= through);
        });
        for (int index = 1; index < fixture.Provider.Requests.Count; index++)
            Assert.True(fixture.Provider.Requests[index - 1].ThroughUtc <= fixture.Provider.Requests[index].FromUtc);
        Assert.Equal(through, fixture.Provider.Requests[^1].ThroughUtc);
        Assert.Equal(originalHash, Assert.Single(fixture.Read(Yesterday).Datasets).DatasetHash);
        Assert.Equal(original, Assert.Single(fixture.Read(Yesterday).Candles));
    }

    [Theory]
    [InlineData(300, 1)]
    [InlineData(315, 2)]
    public async Task BatchesBridgeNoMoreThanFiveMinutesOfSavedCandles(int savedSeconds, int expectedRequests)
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday), through = start.AddSeconds(savedSeconds + 30);
        HistoricalCandle[] original = Enumerable.Range(1, savedSeconds / 15)
            .Select(index => Candle(start.AddSeconds(index * 15))).ToArray();
        fixture.Seed(Yesterday, original);
        fixture.StartOnly(Yesterday, through);

        await fixture.Drain();

        Assert.Equal(expectedRequests, fixture.Provider.Requests.Count);
        Assert.Equal(start, fixture.Provider.Requests[0].FromUtc);
        Assert.Equal(through, fixture.Provider.Requests[^1].ThroughUtc);
        if (expectedRequests == 2)
        {
            Assert.Equal(start.AddSeconds(15), fixture.Provider.Requests[0].ThroughUtc);
            Assert.Equal(through.AddSeconds(-15), fixture.Provider.Requests[1].FromUtc);
        }
        Assert.Equal(original, fixture.Read(Yesterday).Candles);
    }

    [Fact]
    public async Task SavedDayCoverageCountsStoredCandlesInsteadOfCheckedGaps()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Today);
        fixture.Clock.Now = start.AddMinutes(1);
        fixture.Seed(Today, [Candle(start), Candle(start.AddSeconds(30))]);
        fixture.StartOnly(Today, start.AddMinutes(1));
        fixture.Provider.Candles.AddRange([Candle(start), Candle(start.AddSeconds(15)), Candle(start.AddSeconds(30))]);

        await fixture.Collector.TickAsync(true);

        decimal expected = 75m; // Three saved candles out of the four completed candles so far.
        CollectionJob partial = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(expected, partial.SavedCoveragePercent);
        Assert.Equal(CollectionJobStatus.Pending, partial.Status);
        Assert.Equal(start.AddMinutes(1), partial.NextGapFromUtc);
        Assert.Equal(3, fixture.Read(Today).Candles.Count);

        await fixture.Collector.TickAsync(true);
        CollectionJob finished = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Partial, finished.Status);
        Assert.Equal(expected, finished.SavedCoveragePercent);
        Assert.Equal(expected, Assert.Single(fixture.Store.Load().Jobs).SavedCoveragePercent);
    }

    [Fact]
    public async Task RetainedJobsLoadCoverageOfflineWithoutDownloadingOrChangingOutcome()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Yesterday);
        fixture.Seed(Yesterday, [Candle(start), Candle(start.AddSeconds(15))]);
        fixture.StartOnly(Yesterday, Close(Yesterday));
        CollectionState state = fixture.Store.Load();
        fixture.Store.Save(state with { Jobs = state.Jobs.Select(job => job with
        {
            Status = CollectionJobStatus.Partial, SavedCoveragePercent = null,
        }).ToArray() });
        fixture.Restart();

        await fixture.Collector.TickAsync(false);

        CollectionJob job = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Partial, job.Status);
        Assert.Equal(100m * 30 / (decimal)(Close(Yesterday) - start).TotalSeconds, job.SavedCoveragePercent);
        Assert.Equal(job.SavedCoveragePercent, Assert.Single(fixture.Store.Load().Jobs).SavedCoveragePercent);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(2, fixture.Read(Yesterday).Candles.Count);
    }

    [Fact]
    public async Task FailedAttemptRetainsStoredCoverageAndCandlesForTheNextRun()
    {
        using var fixture = new Fixture();
        DateTimeOffset start = Open(Today);
        fixture.Seed(Today, [Candle(start)]);
        fixture.StartOnly(Today, start.AddMinutes(1));
        fixture.Provider.Failure = "http";

        await fixture.Collector.TickAsync(true);

        CollectionJob failed = Assert.Single(fixture.Collector.State.Jobs);
        Assert.Equal(CollectionJobStatus.Failed, failed.Status);
        Assert.Equal(100m, failed.SavedCoveragePercent); // The one completed candle is still saved.
        Assert.Single(fixture.Read(Today).Candles);
        Assert.Empty(fixture.Snapshot(Today).UnavailableRanges);
        await fixture.Collector.RetryMissingAsync();
        Assert.Equal(failed.SavedCoveragePercent, Assert.Single(fixture.Collector.State.Jobs).SavedCoveragePercent);
    }

    private static void AssertRange(HistoricalDataRequest request, DateTimeOffset from, DateTimeOffset through)
    {
        Assert.Equal(from, request.FromUtc);
        Assert.Equal(through, request.ThroughUtc);
    }

    private static HistoricalCandle Candle(DateTimeOffset start) =>
        new(start, start.AddSeconds(15), start.AddSeconds(15), 10m, 11m, 9m, 10m, 100);
    private static DateTimeOffset Open(DateOnly day) => CollectionSchedule.GetSessionWindow(day, "24_5").FromUtc;
    private static DateTimeOffset Close(DateOnly day) => CollectionSchedule.GetSessionWindow(day, "24_5").ThroughUtc;
    private static DateOnly Day(HistoricalDataRequest request) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, Eastern).DateTime);
    private static CollectionGapKey Key(DateOnly day) => new("SOFI", "SOFI-id", day, "24_5", "split", "robinhood-split-unversioned");

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        public Fixture()
        {
            Library = new(Path.Combine(_temporaryRoot, "PriceSentinel-gap-index-integration-" + Guid.NewGuid().ToString("N")));
            Provider = new(Clock);
            Index = new(DatabasePath);
            Store.Save(new() { Settings = new()
            {
                LibraryRootPath = Library.RootPath,
                Lists = [new(Guid.NewGuid(), "Included", true, [new("SOFI", ProviderInstrumentId: "SOFI-id")])],
            } });
            Restart();
        }
        public string DatabasePath => Path.Combine(Library.RootPath, ".collection-gaps.sqlite3");
        public Clock Clock { get; } = new(Open(Today).AddSeconds(15));
        public MemoryStore Store { get; } = new();
        public Provider Provider { get; }
        public JsonMarketDataLibrary Library { get; }
        public SqliteCollectionGapIndex Index { get; }
        public MarketDataCollector Collector { get; private set; } = null!;
        public void Restart() => Collector = new(Store, Provider, _ => Library, Clock, new()
        {
            MaximumRequestsPerTick = 1, MaximumTransientAttempts = 1,
            MinimumRequestInterval = TimeSpan.Zero, RetryDelay = TimeSpan.Zero,
        }, gapIndexFactory: root => new SqliteCollectionGapIndex(Path.Combine(root, ".collection-gaps.sqlite3")));
        public void StartOnly(DateOnly day, DateTimeOffset through, bool availabilityCheck = false)
        {
            Store.Save(Store.Load() with { Jobs = [new()
            {
                Symbol = "SOFI", ProviderInstrumentId = "SOFI-id", SessionDate = day,
                SessionBounds = "24_5", LibraryRootPath = Library.RootPath,
                Status = CollectionJobStatus.Pending, QueuedAtUtc = Clock.Now, RequestedThroughUtc = through,
                IsAvailabilityProbe = availabilityCheck, AvailabilityCheckPending = availabilityCheck,
            }] });
            Restart();
        }
        public void RememberEmpty(DateOnly day, DateTimeOffset from, DateTimeOffset through, DateTimeOffset? retryAfter = null)
        {
            Index.Initialize();
            Index.RecordAttempt(Key(day), from, through, [new(from, through)], false, Clock.Now, retryAfter);
        }
        public CollectionGapSnapshot Snapshot(DateOnly day, DateTimeOffset? through = null)
        {
            Index.Initialize();
            return Index.Query(Key(day), Open(day), through ?? Close(day), Clock.Now);
        }
        public void Seed(DateOnly day, HistoricalCandle[] candles)
        {
            Library.Save(new("test", "SOFI-id", "SOFI", 15, "split", "robinhood-split-unversioned",
                "24_5", Clock.Now, Open(day), Close(day), candles));
        }
        public HistoricalDataQueryResult Read(DateOnly day) => Library.Query(new("SOFI", Open(day), Close(day), 15,
            SessionBounds: "24_5", RevisionPolicy: HistoricalRevisionPolicy.CompatibleCoverage, IncludeCompatibleSessions: true));
        public async Task Drain()
        {
            for (int tick = 0; tick < 300 && Collector.State.Jobs.Any(job =>
                job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading); tick++)
                await Collector.TickAsync(true);
            Assert.DoesNotContain(Collector.State.Jobs, job =>
                job.Status is CollectionJobStatus.Pending or CollectionJobStatus.Downloading or CollectionJobStatus.Failed);
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(Library.RootPath);
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("PriceSentinel-gap-index-integration-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the test's temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : ICollectionStateStore
    {
        private string _json = JsonSerializer.Serialize(new CollectionState());
        public CollectionState Load() => JsonSerializer.Deserialize<CollectionState>(_json)!;
        public void Save(CollectionState state) => _json = JsonSerializer.Serialize(state);
    }

    private sealed class Provider(Clock clock) : IMarketHistoryProvider
    {
        public List<HistoricalDataRequest> Requests { get; } = [];
        public List<HistoricalCandle> Candles { get; } = [];
        public string? Failure { get; set; }
        public Action? BeforeResponse { get; set; }
        public Task<HistoricalDownload> DownloadHistoryAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            BeforeResponse?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure == "http") throw new HttpRequestException("Temporary broker failure.");
            if (Failure == "timeout") throw new TimeoutException("Broker request timed out.");
            if (Failure == "connection") throw new MarketDataConnectionUnavailableException("Broker disconnected.");
            HistoricalCandle[] returned = Candles.Where(candle => candle.StartsAtUtc >= request.FromUtc &&
                candle.EndsAtUtc <= request.ThroughUtc).OrderBy(candle => candle.StartsAtUtc).ToArray();
            return Task.FromResult(new HistoricalDownload("test", "SOFI-id", Failure == "provenance" ? "OTHER" : request.Symbol,
                15, request.AdjustmentPolicy, "robinhood-split-unversioned", request.SessionBounds, clock.Now,
                request.FromUtc, request.ThroughUtc, returned));
        }
    }
}
