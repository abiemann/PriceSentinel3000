using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Authentication;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketData;

public sealed partial class RobinhoodMcpGateway :
    IMarketHistoryProvider, IPersonalWatchlistSource, IEquityCatalogSource
{
    private static readonly AsyncLocal<bool> LibraryReadOnlyScope = new();

    // Transport presence only; an expired/revoked authorization can still fail a
    // read. This does not connect, refresh credentials, or authorize anything.
    public bool HasActiveConnection => _client is not null;

    private static Task<AuthorizationResult?> AuthorizeUnlessLibraryAsync(
        AuthorizationCallbackContext context, CancellationToken cancellationToken) =>
        LibraryReadOnlyScope.Value
            ? DeclineInteractiveAuthorizationAsync(context, cancellationToken)
            : Authentication.RobinhoodBrowserAuthorization.AuthorizeAsync(context, cancellationToken);

    public async Task<IReadOnlyList<PersonalWatchlist>> GetWatchlistsAsync(CancellationToken cancellationToken)
    {
        JsonElement response = await CallLibraryToolAsync("get_watchlists", new Dictionary<string, object?>(), cancellationToken)
            .ConfigureAwait(false);
        return RobinhoodLibraryParser.ParseWatchlists(response);
    }

    public async Task<PersonalWatchlistMembers> GetWatchlistMembersAsync(
        PersonalWatchlist watchlist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(watchlist.Id);
        ArgumentOutOfRangeException.ThrowIfNegative(watchlist.ItemCount);
        JsonElement response = await CallLibraryToolAsync("get_watchlist_items",
            new Dictionary<string, object?> { ["list_id"] = watchlist.Id }, cancellationToken).ConfigureAwait(false);
        return RobinhoodLibraryParser.ParseWatchlistMembers(response, watchlist);
    }

    public async Task<IReadOnlyList<EquityResolution>> ResolveEquitiesAsync(
        IReadOnlyList<string> symbols, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var results = new List<EquityResolution>(symbols.Count);
        foreach (string input in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string symbol = RobinhoodLibraryParser.NormalizeSymbol(input);
            if (!RobinhoodLibraryParser.IsEquitySymbol(symbol))
            {
                results.Add(new(input, symbol, null, null, false, "Enter a stock or equity ETF ticker."));
                continue;
            }
            JsonElement response = await CallLibraryToolAsync("search", new Dictionary<string, object?>
            {
                ["query"] = symbol, ["asset_type"] = "instrument", ["limit"] = 20,
            }, cancellationToken).ConfigureAwait(false);
            results.Add(RobinhoodLibraryParser.ResolveEquity(response, input));
        }
        return results;
    }

    public async Task<HistoricalDownload> DownloadHistoryAsync(
        HistoricalDataRequest request, CancellationToken cancellationToken)
    {
        RobinhoodLibraryParser.ValidateRequest(request);
        if (string.IsNullOrWhiteSpace(request.InstrumentId))
        {
            EquityResolution resolved = (await ResolveEquitiesAsync([request.Symbol], cancellationToken).ConfigureAwait(false))[0];
            if (!resolved.IsSupported)
                throw new InvalidOperationException(resolved.Message ?? "The requested equity could not be resolved.");
            request = request with { InstrumentId = resolved.ProviderInstrumentId };
        }
        JsonElement response = await CallLibraryToolAsync("get_equity_historicals",
            BuildLibraryHistoryArguments(request), cancellationToken).ConfigureAwait(false);
        return RobinhoodLibraryParser.ParseHistory(response, request, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyDictionary<string, object?> BuildLibraryHistoryArguments(HistoricalDataRequest request)
    {
        RobinhoodLibraryParser.ValidateRequest(request);
        return new Dictionary<string, object?>
        {
            ["symbols"] = new[] { RobinhoodLibraryParser.NormalizeSymbol(request.Symbol) },
            ["start_time"] = request.FromUtc.ToUniversalTime().ToString("O"),
            ["end_time"] = request.ThroughUtc.ToUniversalTime().ToString("O"),
            ["interval"] = RobinhoodLibraryParser.IntervalName(request.SourceIntervalSeconds),
            ["bounds"] = request.SessionBounds,
            ["adjustment_type"] = request.AdjustmentPolicy,
        };
    }

    // Library collection must never open interactive authentication from its scheduler.
    private async Task<JsonElement> CallLibraryToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _client ?? throw new MarketDataConnectionUnavailableException("Connect to Robinhood before using the market-data library.");
        CallToolResult result;
        bool previousScope = LibraryReadOnlyScope.Value;
        LibraryReadOnlyScope.Value = true;
        try
        {
            result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LibraryReadOnlyScope.Value = previousScope;
        }
        if (result.IsError is true)
            throw new InvalidOperationException($"Robinhood MCP rejected {toolName}." +
                (toolName is "get_watchlists" or "get_watchlist_items" ? " Personal-list access may be unavailable; manual ticker lists remain available." : string.Empty));
        return result.StructuredContent ?? throw new InvalidOperationException($"Robinhood returned no structured data for {toolName}.");
    }
}
