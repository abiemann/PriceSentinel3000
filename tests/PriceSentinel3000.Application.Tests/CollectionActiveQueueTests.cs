using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Application.Tests;

public sealed partial class MarketDataCollectorTests
{
    [Fact]
    public async Task ActiveQueue_MultiRequestJobsStayWithinEightUntilACompletedJobFreesItsSlot()
    {
        var (collector, _, provider, library, clock) = Create(Options() with { MaximumRequestsPerTick = 100 });
        DateOnly day = Day.AddDays(-1);
        string[] symbols = Enumerable.Range(0, 12).Select(index => $"EQ{index:00}").ToArray();
        await collector.QueueManualAsync(symbols, day, day, "24_5");

        Assert.Equal(CollectionBatchResult.Ready, await collector.TickAsync(true));
        Guid[] active = collector.State.ActiveJobIds.ToArray();
        Assert.Equal(8, active.Length);
        Assert.Equal(symbols.Take(8), provider.Requests.Select(request => request.Symbol));
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(CollectionBatchResult.Ready, await collector.TickAsync(true));
        Assert.Equal(active, collector.State.ActiveJobIds);
        Assert.Equal(symbols.Take(8), provider.Requests.Skip(8).Select(request => request.Symbol));
        Assert.All(collector.State.Jobs, job => Assert.Equal(CollectionJobStatus.Pending, job.Status));
        Assert.All(collector.State.Jobs.Skip(8), job => Assert.Null(job.LastAttemptAtUtc));

        // Complete one admitted day from saved data while the other seven still need later sections.
        var seed = new Provider();
        foreach (CollectionSessionWindow window in CollectionSchedule.GetSessionWindows(day, "24_5"))
            library.Save(await seed.DownloadHistoryAsync(new(symbols[0], window.FromUtc, window.ThroughUtc,
                15, "24_5", "split"), default));
        clock.Now = clock.Now.AddSeconds(1);
        await collector.TickAsync(true);
        Assert.Equal(CollectionJobStatus.Complete, collector.State.Jobs[0].Status);
        Guid replacement = collector.State.Jobs[8].Id;
        Assert.Equal(active.Skip(1).Append(replacement), collector.State.ActiveJobIds);
        clock.Now = clock.Now.AddSeconds(1);
        await collector.TickAsync(true);
        Assert.Contains(provider.Requests, request => request.Symbol == symbols[8]);
        Assert.DoesNotContain(provider.Requests, request => symbols.Skip(9).Contains(request.Symbol));
        Assert.InRange(collector.State.ActiveJobIds.Count, 0, 8);
    }

    [Fact]
    public async Task ActiveQueue_RetryWaitsHoldAllEightSlotsAndReportWaitingDespiteUntouchedPendingJobs()
    {
        var (collector, _, provider, _, clock) = Create(Options() with { RetryDelay = TimeSpan.FromMinutes(1) });
        await collector.QueueManualAsync(["OLD0", "OLD1", "OLD2", "OLD3"], Day.AddDays(-1), Day.AddDays(-1), "24_5");
        clock.Now = clock.Now.AddSeconds(1);
        string[] newest = Enumerable.Range(0, 8).Select(index => $"NEW{index}").ToArray();
        await collector.QueueManualAsync(newest, Day, Day, "24_5");
        provider.OnRequest = () => throw new HttpRequestException("Temporary transport failure");

        Assert.Equal(CollectionBatchResult.WaitingForRetry, await collector.TickAsync(true));
        Guid[] active = collector.State.Jobs.Where(job => job.SessionDate == Day).Select(job => job.Id).ToArray();
        Assert.Equal(active, collector.State.ActiveJobIds);
        Assert.Equal(newest, provider.Requests.Select(request => request.Symbol));
        Assert.All(collector.State.Jobs.Where(job => active.Contains(job.Id)), job =>
        {
            Assert.Equal(CollectionJobStatus.Pending, job.Status);
            Assert.Equal(clock.Now.AddMinutes(1), job.RetryAfterUtc);
        });
        Assert.All(collector.State.Jobs.Where(job => !active.Contains(job.Id)), job =>
        {
            Assert.Equal(CollectionJobStatus.Pending, job.Status);
            Assert.Null(job.LastAttemptAtUtc);
            Assert.Null(job.RetryAfterUtc);
        });

        Assert.Equal(CollectionBatchResult.WaitingForRetry, await collector.TickAsync(true));
        Assert.Equal(8, provider.Requests.Count);
        Assert.Equal(active, collector.State.ActiveJobIds);
    }

    [Fact]
    public async Task ActiveQueue_RestartCancellationAndNewerManualWorkPreserveTheAdmittedJobs()
    {
        CollectionRunOptions options = Options() with { MaximumRequestsPerTick = 1 };
        var (collector, store, provider, library, clock) = Create(options);
        string[] symbols = Enumerable.Range(0, 12).Select(index => $"EQ{index:00}").ToArray();
        await collector.QueueManualAsync(symbols, Day.AddDays(-1), Day.AddDays(-1), "24_5");
        await collector.TickAsync(true);
        Guid[] active = collector.State.ActiveJobIds.ToArray();
        Assert.Equal(8, active.Length);
        clock.Now = clock.Now.AddSeconds(1);
        await collector.QueueManualAsync(["NEWER"], Day, Day, "24_5");
        Assert.Equal(active, collector.State.ActiveJobIds);

        var restarted = new MarketDataCollector(store, provider, _ => library, clock, options);
        Assert.Equal(active, restarted.State.ActiveJobIds);
        using var cancellation = new CancellationTokenSource();
        provider.OnRequest = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.TickAsync(true, cancellation.Token));
        Assert.Equal(active, store.Load().ActiveJobIds);
        Assert.DoesNotContain(store.Load().Jobs, job => job.Status == CollectionJobStatus.Downloading);

        provider.OnRequest = null;
        clock.Now = clock.Now.AddSeconds(1);
        var resumed = new MarketDataCollector(store, provider, _ => library, clock, options);
        await resumed.TickAsync(true);
        Assert.Equal(active, resumed.State.ActiveJobIds);
        Assert.All(provider.Requests, request => Assert.Contains(request.Symbol, symbols.Take(8)));
        Assert.All(resumed.State.Jobs.Where(job => !active.Contains(job.Id)), job => Assert.Null(job.LastAttemptAtUtc));
        Assert.Equal(3, provider.Requests.Count);
    }
}
