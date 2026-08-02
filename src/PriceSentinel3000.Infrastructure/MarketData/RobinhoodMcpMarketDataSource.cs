using System.Text.Json;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Infrastructure.Authentication;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Infrastructure.MarketData;

public sealed class RobinhoodMcpMarketDataSource : IMarketDataSource
{
    private static readonly Uri Endpoint =
        new("https://agent.robinhood.com/mcp/trading");
    internal static TimeSpan InteractiveAuthorizationTimeout { get; } =
        TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ProtectedRobinhoodAuthStore _authStore;
    private McpClient? _client;

    public RobinhoodMcpMarketDataSource(
        ProtectedRobinhoodAuthStore authStore)
    {
        _authStore = authStore ?? throw new ArgumentNullException(nameof(authStore));
    }

    public string Name => "ROBINHOOD MCP";

    public static RobinhoodMcpMarketDataSource CreateDefault() =>
        new(new ProtectedRobinhoodAuthStore(
            AppDataPaths.RobinhoodTokenCache,
            AppDataPaths.RobinhoodClientRegistration));

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_client is not null)
            {
                return;
            }

            DynamicClientRegistrationResponse? registration =
                _authStore.ReadRegistration();
            var oauth = new ClientOAuthOptions
            {
                RedirectUri = RobinhoodBrowserAuthorization.RedirectUri,
                AuthorizationRedirectDelegate =
                    RobinhoodBrowserAuthorization.AuthorizeAsync,
                TokenCache = _authStore,
                ClientId = registration?.ClientId,
                ClientSecret = registration?.ClientSecret,
                DynamicClientRegistration = registration is null
                    ? new DynamicClientRegistrationOptions
                    {
                        ClientName = "PriceSentinel 3000",
                        ClientUri = new Uri(
                            "https://github.com/abiemann/PriceSentinel3000"),
                        ResponseDelegate = _authStore.StoreRegistrationAsync,
                    }
                    : null,
            };
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                Name = "PriceSentinel3000-Robinhood",
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                OAuth = oauth,
            });

            try
            {
                var clientOptions = new McpClientOptions
                {
                    InitializationTimeout = InteractiveAuthorizationTimeout,
                };
                _client = await McpClient.CreateAsync(
                        transport,
                        clientOptions,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<MarketQuote>> GetHistoryAsync(
        MarketDataRequest request,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (fromUtc > throughUtc)
        {
            throw new ArgumentException(
                "History start must not be after its end.",
                nameof(fromUtc));
        }

        JsonElement root = await CallStructuredToolAsync(
            "get_equity_historicals",
            new Dictionary<string, object?>
            {
                ["symbols"] = new[] { request.Instrument.Symbol },
                ["start_time"] = fromUtc.ToUniversalTime().ToString("O"),
                ["end_time"] = throughUtc.ToUniversalTime().ToString("O"),
                ["interval"] = "15second",
                ["bounds"] = "extended",
                ["adjustment_type"] = "split",
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodMarketDataParser.ParseHistory(
            root,
            request.Instrument,
            observedAtUtc);
    }

    public async Task<MarketQuote> GetQuoteAsync(
        MarketDataRequest request,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_quotes",
            new Dictionary<string, object?>
            {
                ["symbols"] = new[] { request.Instrument.Symbol },
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodMarketDataParser.ParseQuote(
            root,
            request.Instrument,
            observedAtUtc);
    }

    public async Task<IReadOnlyList<MarketQuote>> GetReplayHistoryAsync(
        Instrument instrument,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (fromUtc > throughUtc)
        {
            throw new ArgumentException(
                "Replay start must not be after its end.",
                nameof(fromUtc));
        }

        DateTimeOffset start = fromUtc.ToUniversalTime();
        DateTimeOffset end = throughUtc.ToUniversalTime();
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_historicals",
            new Dictionary<string, object?>
            {
                ["symbols"] = new[] { instrument.Symbol },
                ["start_time"] = start.ToString("O"),
                ["end_time"] = end.ToString("O"),
                ["interval"] = "15second",
                ["bounds"] = "extended",
                ["adjustment_type"] = "split",
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodMarketDataParser.ParseHistory(
                root,
                instrument,
                observedAtUtc)
            .Where(quote =>
                quote.SourceTimestampUtc >= start &&
                quote.SourceTimestampUtc <= end)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);

        try
        {
            McpClient? client = _client;
            _client = null;

            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private async Task<JsonElement> CallStructuredToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        CallToolResult result = await _client!.CallToolAsync(
                toolName,
                arguments,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError is true)
        {
            throw new InvalidOperationException(
                $"Robinhood MCP rejected {toolName}.");
        }

        return result.StructuredContent ?? throw new InvalidOperationException(
            $"Robinhood MCP returned no structured data for {toolName}.");
    }

}
