namespace PriceSentinel3000.Core.MarketData;

public interface ICachedAuthenticationMarketDataSource
{
    bool HasCachedAuthentication { get; }

    Task<bool> TryConnectUsingCachedAuthenticationAsync(
        CancellationToken cancellationToken);
}
