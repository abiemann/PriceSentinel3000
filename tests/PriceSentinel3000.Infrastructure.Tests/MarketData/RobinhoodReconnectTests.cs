using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.Authentication;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodReconnectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PriceSentinel-reconnect-tests", Guid.NewGuid().ToString("N"));
    private ProtectedRobinhoodAuthStore Store => new(Path.Combine(_root, "tokens.dat"), Path.Combine(_root, "client.dat"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialization_IdentifiesPriceSentinelForInteractiveAndCachedConnections(bool allowInteractive)
    {
        var transport = new FakeTransport();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using McpClient client = await McpClient.CreateAsync(transport,
            RobinhoodMcpGateway.CreateClientOptions(allowInteractive), cancellationToken: cancellation.Token);

        JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(transport.InitializeRequest);
        Assert.Equal("PriceSentinel", request.Params!["clientInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("PriceSentinel", request.Params["clientInfo"]!["title"]!.GetValue<string>());
        Assert.Equal(PriceSentinel3000.Application.BuildVersion.Display(typeof(RobinhoodMcpGateway).Assembly),
            request.Params["clientInfo"]!["version"]!.GetValue<string>());
        Assert.Equal(RobinhoodMcpGateway.RobinhoodProtocolVersion, request.Params["protocolVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitReconnect_RejectsActiveLibraryAndMarketCallsWithoutDisposingThem(bool libraryCall)
    {
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(Store, (_, token) => CreateClient(transports, token));
        await gateway.ConnectAsync(default);
        FakeTransport original = Assert.Single(transports);
        original.HoldTools = true;
        Task pending = libraryCall ? gateway.GetWatchlistsAsync(default) : gateway.SearchAsync("MSFT", default);
        await original.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReconnectAsync(default));
        Assert.Contains("still running", error.Message);
        Assert.False(original.Disposed);
        Assert.True(gateway.HasActiveConnection);
        Assert.Single(transports);

        original.ReleaseTool();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await gateway.ReconnectAsync(default);
        Assert.True(original.Disposed);
        Assert.Equal(2, transports.Count);
        Assert.True(gateway.HasActiveConnection);
        Assert.Empty(await gateway.GetWatchlistsAsync(default));
    }

    [Fact]
    public async Task ExplicitReconnect_ReplacesCachedOnlyTransportWithInteractiveCapableConnection()
    {
        ProtectedRobinhoodAuthStore store = Store;
        await store.StoreTokensAsync(new TokenContainer
        {
            AccessToken = "synthetic-test-token", TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow, ExpiresIn = 3600,
        }, default);
        await store.StoreRegistrationAsync(new DynamicClientRegistrationResponse { ClientId = "synthetic-client" }, default);
        var interactive = new List<bool>();
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(store, (allowInteractive, token) =>
        {
            interactive.Add(allowInteractive);
            return CreateClient(transports, token);
        });

        Assert.True(await gateway.TryConnectUsingCachedAuthenticationAsync(default));
        FakeTransport cached = Assert.Single(transports);
        await gateway.ConnectAsync(default); // The ordinary path retains an existing transport.
        Assert.Equal(new[] { false }, interactive);
        await gateway.ReconnectAsync(default);

        Assert.Equal(new[] { false, true }, interactive);
        Assert.True(cached.Disposed);
        Assert.Empty(await gateway.GetWatchlistsAsync(default));
        Assert.True(store.HasCachedAuthentication); // Reconnect does not erase credentials.
    }

    [Fact]
    public async Task CancelledReconnect_LeavesExistingTransportIntact()
    {
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(Store, (_, token) => CreateClient(transports, token));
        await gateway.ConnectAsync(default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.ReconnectAsync(cancellation.Token));
        Assert.False(Assert.Single(transports).Disposed);
        Assert.True(gateway.HasActiveConnection);
    }

    [Fact]
    public async Task CancelledLibraryRequest_ReleasesItsLeaseForExplicitReconnect()
    {
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(Store, (_, token) => CreateClient(transports, token));
        await gateway.ConnectAsync(default);
        FakeTransport original = Assert.Single(transports);
        original.HoldTools = true;
        using var cancellation = new CancellationTokenSource();
        Task request = gateway.GetWatchlistsAsync(cancellation.Token);
        await original.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await gateway.ReconnectAsync(default);
        Assert.True(original.Disposed);
    }

    [Fact]
    public async Task MarketHours_UsesSharedWatchlistCacheAndRefreshesAfterReconnect()
    {
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(Store, (_, token) => CreateClient(transports, token));
        await gateway.ConnectAsync(default);
        FakeTransport original = Assert.Single(transports);
        original.ToolReply = MarketHoursReply;
        IEquityMarketHoursSource source = gateway;

        Assert.True(await source.IsTwentyFourHourEligibleAsync(" nvda ", default));
        Assert.True(await source.IsTwentyFourHourEligibleAsync("AAPL", default));
        Assert.False(await source.IsTwentyFourHourEligibleAsync("SMALL", default));
        Assert.Equal(new[] { "get_popular_watchlists", "get_watchlist_items" }, original.ToolCalls);

        await gateway.ReconnectAsync(default);
        FakeTransport reconnected = transports[1];
        reconnected.ToolReply = MarketHoursReply;
        Assert.True(await source.IsTwentyFourHourEligibleAsync("NVDA", default));
        Assert.Equal(new[] { "get_popular_watchlists", "get_watchlist_items" }, reconnected.ToolCalls);
    }

    [Theory]
    [InlineData("api-error")]
    [InlineData("missing-list")]
    [InlineData("invalid-items")]
    [InlineData("empty-items")]
    public async Task MarketHours_UnknownResponsesThrowWithoutCachingIneligibility(string failure)
    {
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(Store, (_, token) => CreateClient(transports, token));
        await gateway.ConnectAsync(default);
        FakeTransport transport = Assert.Single(transports);
        transport.ToolReply = name => failure switch
        {
            "missing-list" => ToolResult(new { data = new { lists = Array.Empty<object>() } }),
            _ when name == "get_popular_watchlists" => MarketHoursReply(name),
            "api-error" => new { content = Array.Empty<object>(), isError = true },
            "empty-items" => ToolResult(new { data = new { items = Array.Empty<object>() } }),
            _ => ToolResult(new { data = new { } }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.IsTwentyFourHourEligibleAsync("NVDA", default));
        transport.ToolReply = MarketHoursReply;
        Assert.True(await gateway.IsTwentyFourHourEligibleAsync("NVDA", default));
        Assert.Equal(2, transport.ToolCalls.Count(name => name == "get_popular_watchlists"));
    }

    [Fact]
    public async Task MarketHours_DisconnectedLookupDoesNotStartAuthentication()
    {
        int connectionAttempts = 0;
        await using var gateway = new RobinhoodMcpGateway(Store, (_, _) =>
        {
            connectionAttempts++;
            throw new InvalidOperationException("No connection should be started by a library lookup.");
        });

        await Assert.ThrowsAsync<MarketDataConnectionUnavailableException>(() =>
            gateway.IsTwentyFourHourEligibleAsync("NVDA", default));
        Assert.Equal(0, connectionAttempts);
    }

    [Fact]
    public async Task MarketHours_CancelledLookupHonorsCancellationEvenWithWarmCache()
    {
        var transports = new List<FakeTransport>();
        await using var gateway = new RobinhoodMcpGateway(Store, (_, token) => CreateClient(transports, token));
        await gateway.ConnectAsync(default);
        FakeTransport transport = Assert.Single(transports);
        transport.ToolReply = MarketHoursReply;
        Assert.True(await gateway.IsTwentyFourHourEligibleAsync("NVDA", default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.IsTwentyFourHourEligibleAsync("NVDA", cancellation.Token));
        Assert.Equal(2, transport.ToolCalls.Count);
    }

    private static object MarketHoursReply(string name) => name == "get_popular_watchlists"
        ? ToolResult(new { data = new { lists = new[] { new { id = "overnight", display_name = "24 Hour Market" } } } })
        : ToolResult(new { data = new { items = new[]
        {
            new { object_type = "instrument", symbol = "NVDA" },
            new { object_type = "instrument", symbol = "AAPL" },
        } } });

    private static object ToolResult(object data) => new { content = Array.Empty<object>(), structuredContent = data };

    private static async Task<McpClient> CreateClient(List<FakeTransport> transports, CancellationToken token)
    {
        var transport = new FakeTransport();
        transports.Add(transport);
        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = RobinhoodMcpGateway.RobinhoodProtocolVersion,
            InitializationTimeout = TimeSpan.FromSeconds(5),
        }, cancellationToken: token);
    }

    private sealed class FakeTransport : IClientTransport, ITransport
    {
        private readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>();
        private JsonRpcRequest? _held;
        public string Name => "Synthetic reconnect test";
        public string? SessionId => null;
        public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;
        public JsonRpcRequest? InitializeRequest { get; private set; }
        public bool Disposed { get; private set; }
        public bool HoldTools { get; set; }
        public Func<string, object>? ToolReply { get; set; }
        public List<string> ToolCalls { get; } = [];
        public TaskCompletionSource ToolStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult<ITransport>(this);
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is not JsonRpcRequest request) return Task.CompletedTask;
            if (request.Method == "initialize")
            {
                InitializeRequest = request;
                Reply(request, new
                {
                    protocolVersion = RobinhoodMcpGateway.RobinhoodProtocolVersion,
                    capabilities = new { tools = new { } }, serverInfo = new { name = "synthetic", version = "1" },
                });
            }
            else if (request.Method == "tools/call")
            {
                if (HoldTools) _held = request;
                else ReplyTool(request);
                ToolStarted.TrySetResult();
            }
            return Task.CompletedTask;
        }
        public void ReleaseTool() { ReplyTool(_held!); _held = null; }
        private void ReplyTool(JsonRpcRequest request)
        {
            string name = request.Params!["name"]!.GetValue<string>();
            ToolCalls.Add(name);
            Reply(request, ToolReply?.Invoke(name) ?? new
            {
                content = Array.Empty<object>(), structuredContent = new { data = new { watchlists = Array.Empty<object>(), results = Array.Empty<object>() } },
            });
        }
        private void Reply(JsonRpcRequest request, object result) => _messages.Writer.TryWrite(new JsonRpcResponse
        {
            Id = request.Id, Result = JsonSerializer.SerializeToNode(result),
        });
        public ValueTask DisposeAsync() { Disposed = true; _messages.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
