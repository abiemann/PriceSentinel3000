using ModelContextProtocol.Client;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketData;

public sealed partial class RobinhoodMcpGateway
{
    private int _activeToolCalls;

    /// <summary>
    /// Explicit user recovery only. The host must also prevent reconnect while a
    /// trading session starts/runs. Existing requests are never interrupted.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _activeToolCalls) != 0)
                throw new InvalidOperationException("A Robinhood request is still running. Wait for it to finish before reconnecting.");
            McpClient? previous = _client;
            _client = null;
            _twentyFourHourEligibleSymbols = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally { _connectionGate.Release(); }
        // A cached-only transport permanently declines browser authorization.
        // Recreate it with the normal explicit-login authorization callback.
        await ConnectCoreAsync(allowInteractiveAuthorization: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpClient> AcquireToolClientAsync(bool allowConnect, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (allowConnect) await ConnectAsync(cancellationToken).ConfigureAwait(false);
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_client is { } client)
                {
                    Interlocked.Increment(ref _activeToolCalls);
                    return client;
                }
                if (!allowConnect)
                    throw new MarketDataConnectionUnavailableException("Connect to Robinhood before using the market-data library.");
                // An explicit reconnect won the race after ConnectAsync. Retry
                // against its new transport without using the disposed instance.
            }
            finally { _connectionGate.Release(); }
        }
    }

    private void ReleaseToolClient() => Interlocked.Decrement(ref _activeToolCalls);
}
