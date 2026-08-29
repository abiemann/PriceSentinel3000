using PriceSentinel3000.Application.Sessions;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Application.Tests.Sessions;

public sealed class ReplaySessionRunnerTests
{
    private static readonly Instrument Instrument = new("SOFI");
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CalculateDelay_AppliesSpeedAndSafetyBounds()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            ReplaySessionRunner.CalculateDelay(Start, Start.AddSeconds(1), speed: 2m));
        Assert.Equal(
            TimeSpan.FromMilliseconds(20),
            ReplaySessionRunner.CalculateDelay(Start, Start, speed: 1m));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            ReplaySessionRunner.CalculateDelay(Start, Start.AddSeconds(10), speed: 1m));
    }

    [Fact]
    public async Task RunAsync_ReportsIndexTotalAndQuoteInOrder()
    {
        var runner = new ReplaySessionRunner();
        MarketQuote[] quotes = [Quote(10m), Quote(11m)];
        var updates = new List<ReplaySessionUpdate>();

        await foreach (ReplaySessionUpdate update in runner.RunAsync(
                           quotes,
                           speed: 100m,
                           CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Collection(
            updates,
            update =>
            {
                Assert.Equal(0, update.Index);
                Assert.Equal(2, update.Total);
                Assert.Same(quotes[0], update.Quote);
            },
            update =>
            {
                Assert.Equal(1, update.Index);
                Assert.Equal(2, update.Total);
                Assert.Same(quotes[1], update.Quote);
            });
    }

    [Fact]
    public async Task RunAsync_WaitsAtTheFirstQuoteUntilResumed()
    {
        var runner = new ReplaySessionRunner();
        Assert.True(runner.Pause());
        Assert.False(runner.Pause());
        await using IAsyncEnumerator<ReplaySessionUpdate> enumerator = runner
            .RunAsync([Quote(10m)], speed: 1m, CancellationToken.None)
            .GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();

        Assert.False(moveNext.IsCompleted);
        Assert.True(runner.IsPaused);
        Assert.True(runner.Resume());
        Assert.True(await moveNext);
        Assert.False(runner.IsPaused);
        Assert.False(runner.Resume());
    }

    private static MarketQuote Quote(decimal last) =>
        new(
            Instrument,
            Start,
            Start,
            last - 0.01m,
            last + 0.01m,
            last,
            1_000m);
}
