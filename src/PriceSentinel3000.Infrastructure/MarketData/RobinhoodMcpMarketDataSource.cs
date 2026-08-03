using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Infrastructure.Authentication;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Infrastructure.MarketData;

public sealed class RobinhoodMcpMarketDataSource :
    IMarketDataSource,
    ICachedAuthenticationMarketDataSource,
    ILiveBrokerGateway
{
    private static readonly Uri Endpoint =
        new("https://agent.robinhood.com/mcp/trading");
    internal const string RobinhoodProtocolVersion = "2025-11-25";
    internal const string EquityHistoricalBounds = "24_5";
    internal static TimeSpan InteractiveAuthorizationTimeout { get; } =
        TimeSpan.FromMinutes(5);
    internal static TimeSpan CachedAuthorizationTimeout { get; } =
        TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ProtectedRobinhoodAuthStore _authStore;
    private McpClient? _client;

    public RobinhoodMcpMarketDataSource(
        ProtectedRobinhoodAuthStore authStore)
    {
        _authStore = authStore ?? throw new ArgumentNullException(nameof(authStore));
    }

    public string Name => "ROBINHOOD MCP";

    public bool HasCachedAuthentication =>
        _authStore.HasCachedAuthentication;

    public static RobinhoodMcpMarketDataSource CreateDefault() =>
        new(new ProtectedRobinhoodAuthStore(
            AppDataPaths.RobinhoodTokenCache,
            AppDataPaths.RobinhoodClientRegistration));

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        ConnectCoreAsync(allowInteractiveAuthorization: true, cancellationToken);

    public async Task<bool> TryConnectUsingCachedAuthenticationAsync(
        CancellationToken cancellationToken)
    {
        if (!HasCachedAuthentication)
        {
            return false;
        }

        try
        {
            await ConnectCoreAsync(
                    allowInteractiveAuthorization: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ConnectCoreAsync(
        bool allowInteractiveAuthorization,
        CancellationToken cancellationToken)
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
                AuthorizationCallbackHandler = allowInteractiveAuthorization
                    ? RobinhoodBrowserAuthorization.AuthorizeAsync
                    : DeclineInteractiveAuthorizationAsync,
                TokenCache = _authStore,
                ClientId = registration?.ClientId,
                ClientSecret = registration?.ClientSecret,
                DynamicClientRegistration = registration is null
                    ? new DynamicClientRegistrationOptions
                    {
                        ClientName = "PriceSentinel 3000",
                        ClientUri = new Uri(
                            "https://github.com/abiemann/PriceSentinel3000"),
                        ApplicationType = "native",
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
                    ProtocolVersion = RobinhoodProtocolVersion,
                    InitializationTimeout = allowInteractiveAuthorization
                        ? InteractiveAuthorizationTimeout
                        : CachedAuthorizationTimeout,
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

    private static Task<AuthorizationResult?>
        DeclineInteractiveAuthorizationAsync(
            AuthorizationCallbackContext callbackContext,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callbackContext);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<AuthorizationResult?>(null);
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
                ["bounds"] = EquityHistoricalBounds,
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
                ["bounds"] = EquityHistoricalBounds,
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

    public async Task<BrokerAccount> GetAgenticAccountAsync(
        CancellationToken cancellationToken)
    {
        JsonElement root = await CallStructuredToolAsync(
            "get_accounts",
            new Dictionary<string, object?>(),
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParseAgenticAccount(root);
    }

    public async Task<BrokerPortfolio> GetPortfolioAsync(
        string accountNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        JsonElement root = await CallStructuredToolAsync(
            "get_portfolio",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParsePortfolio(root);
    }

    public async Task<BrokerPosition> GetPositionAsync(
        string accountNumber,
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentNullException.ThrowIfNull(instrument);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_positions",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParsePosition(root, instrument.Symbol);
    }

    public async Task<EquityTradability> GetTradabilityAsync(
        string accountNumber,
        string accountType,
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentNullException.ThrowIfNull(instrument);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_tradability",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
                ["symbols"] = new[] { instrument.Symbol },
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParseTradability(
            root,
            instrument.Symbol,
            accountType);
    }

    public async Task<IReadOnlyList<BrokerOrderSnapshot>> GetOpenOrdersAsync(
        string accountNumber,
        Instrument instrument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentNullException.ThrowIfNull(instrument);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_orders",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
                ["symbol"] = instrument.Symbol,
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParseOrders(root)
            .Where(order => order.IsOpen)
            .ToArray();
    }

    public async Task<IReadOnlyList<BrokerOrderSnapshot>> GetOrdersCreatedSinceAsync(
        string accountNumber,
        DateTimeOffset createdAtGteUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_orders",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
                ["created_at_gte"] = createdAtGteUtc.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture),
                ["placed_agent"] = "agentic",
            },
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParseOrders(root);
    }

    public async Task<BrokerOrderReview> ReviewOrderAsync(
        string accountNumber,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentNullException.ThrowIfNull(intent);
        JsonElement root = await CallStructuredToolAsync(
            "review_equity_order",
            OrderArguments(accountNumber, intent, includeReferenceId: false),
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParseReview(root, intent);
    }

    public async Task<BrokerOrderSnapshot> PlaceOrderAsync(
        string accountNumber,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentNullException.ThrowIfNull(intent);
        JsonElement root = await CallStructuredToolAsync(
            "place_equity_order",
            OrderArguments(accountNumber, intent, includeReferenceId: true),
            cancellationToken).ConfigureAwait(false);
        return RobinhoodBrokerParser.ParsePlacedOrder(
            root,
            intent.ClientReferenceId);
    }

    public async Task<BrokerOrderSnapshot> GetOrderAsync(
        string accountNumber,
        string brokerOrderId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_orders",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
                ["order_id"] = brokerOrderId,
            },
            cancellationToken).ConfigureAwait(false);
        BrokerOrderSnapshot? order = RobinhoodBrokerParser.ParseOrders(root)
            .FirstOrDefault(item => string.Equals(
                item.BrokerOrderId,
                brokerOrderId,
                StringComparison.OrdinalIgnoreCase));
        return order ?? throw new InvalidOperationException(
            $"Robinhood returned no state for order {brokerOrderId}.");
    }

    public async Task<BrokerOrderSnapshot?> FindOrderByClientReferenceAsync(
        string accountNumber,
        Instrument instrument,
        Guid clientReferenceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentNullException.ThrowIfNull(instrument);
        JsonElement root = await CallStructuredToolAsync(
            "get_equity_orders",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
                ["symbol"] = instrument.Symbol,
            },
            cancellationToken).ConfigureAwait(false);
        BrokerOrderSnapshot? order = RobinhoodBrokerParser.ParseOrders(root)
            .FirstOrDefault(item =>
                item.ClientReferenceId == clientReferenceId);
        return order is null
            ? null
            : order with
            {
                ClientReferenceId = clientReferenceId,
            };
    }

    public async Task CancelOrderAsync(
        string accountNumber,
        string brokerOrderId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        JsonElement root = await CallStructuredToolAsync(
            "cancel_equity_order",
            new Dictionary<string, object?>
            {
                ["account_number"] = accountNumber,
                ["order_id"] = brokerOrderId,
            },
            cancellationToken).ConfigureAwait(false);

        if (!RobinhoodBrokerParser.ParseCancellationAccepted(root))
        {
            throw new InvalidOperationException(
                $"Robinhood did not accept cancellation for order {brokerOrderId}.");
        }
    }

    private static IReadOnlyDictionary<string, object?> OrderArguments(
        string accountNumber,
        BrokerOrderIntent intent,
        bool includeReferenceId)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["account_number"] = accountNumber,
            ["symbol"] = intent.Symbol,
            ["side"] = intent.Side is BrokerOrderSide.Buy ? "buy" : "sell",
            ["quantity"] = intent.Quantity.ToString("0.######", CultureInfo.InvariantCulture),
            ["type"] = "market",
            ["market_hours"] = "regular_hours",
            ["time_in_force"] = "gfd",
        };

        if (includeReferenceId)
        {
            arguments["ref_id"] = intent.ClientReferenceId.ToString("D");
        }

        return arguments;
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
