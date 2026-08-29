using PriceSentinel3000.Application.Sessions;

namespace PriceSentinel3000.Application.Tests.Sessions;

public sealed class TradingSessionCoordinatorTests
{
    [Fact]
    public void Begin_CancelsAndReplacesThePreviousSession()
    {
        using var coordinator = new TradingSessionCoordinator();

        CancellationToken first = coordinator.Begin();
        CancellationToken second = coordinator.Begin();

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.True(coordinator.IsActive);
        Assert.Equal(second, coordinator.Token);
    }

    [Fact]
    public void Cancel_CancelsTheActiveSessionAndClearsState()
    {
        using var coordinator = new TradingSessionCoordinator();
        CancellationToken token = coordinator.Begin();

        coordinator.Cancel();

        Assert.True(token.IsCancellationRequested);
        Assert.False(coordinator.IsActive);
        Assert.Equal(CancellationToken.None, coordinator.Token);
    }

    [Fact]
    public void Dispose_CancelsAndPreventsAnotherSession()
    {
        var coordinator = new TradingSessionCoordinator();
        CancellationToken token = coordinator.Begin();

        coordinator.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Begin());
    }
}
