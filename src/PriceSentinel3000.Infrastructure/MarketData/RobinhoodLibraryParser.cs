using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketData;

internal static partial class RobinhoodLibraryParser
{
    internal static string NormalizeSymbol(string? symbol) => symbol?.Trim().ToUpperInvariant() ?? string.Empty;
    internal static bool IsEquitySymbol(string symbol) => EquitySymbolPattern().IsMatch(symbol);

    [GeneratedRegex("^[A-Z][A-Z0-9.-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex EquitySymbolPattern();

    internal static string IntervalName(int interval) => interval switch
    {
        15 => "15second", 30 => "30second", 60 => "minute",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), "Supported historical source intervals are 15, 30, and 60 seconds."),
    };

    internal static void ValidateRequest(HistoricalDataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsEquitySymbol(NormalizeSymbol(request.Symbol))) throw new ArgumentException("A valid equity ticker is required.", nameof(request));
        _ = IntervalName(request.SourceIntervalSeconds);
        if (request.FromUtc >= request.ThroughUtc) throw new ArgumentException("History end must be after its start.", nameof(request));
        if (request.SessionBounds is not ("regular" or "extended" or "24_5")) throw new ArgumentException("Choose regular, extended, or 24_5 session bounds.", nameof(request));
        if (request.AdjustmentPolicy != "split") throw new ArgumentException("The library provider requires split adjustment.", nameof(request));
    }

    internal static IReadOnlyList<PersonalWatchlist> ParseWatchlists(JsonElement root)
    {
        JsonElement lists = Array(root, "watchlists");
        EnsureComplete(root, lists.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PersonalWatchlist>();
        foreach (JsonElement item in lists.EnumerateArray())
        {
            string id = RequiredText(item, "id");
            string name = RequiredText(item, "display_name");
            if (!seen.Add(id)) throw new InvalidOperationException("Robinhood returned duplicate watchlist identities.");
            if (!item.TryGetProperty("item_count", out var count) || !count.TryGetInt32(out int expected) || expected < 0)
                throw new InvalidOperationException("Robinhood did not provide a valid watchlist item count.");
            result.Add(new(id, name, expected));
        }
        return result;
    }

    internal static PersonalWatchlistMembers ParseWatchlistMembers(JsonElement root, PersonalWatchlist watchlist)
    {
        JsonElement items = Array(root, "items");
        EnsureComplete(root, items.GetArrayLength());
        if (items.GetArrayLength() != watchlist.ItemCount)
            throw new InvalidOperationException("Robinhood watchlist retrieval is incomplete or its membership changed. Refresh the lists and retry.");
        var members = new List<PersonalWatchlistMember>();
        var identities = new HashSet<(string Type, string Id)>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            string type = RequiredText(item, "object_type").ToLowerInvariant();
            string? symbol = Text(item, "symbol") is { } ticker ? NormalizeSymbol(ticker) : null;
            string? id = Text(item, "object_id");
            if (type == "instrument" && (!IsEquitySymbol(symbol ?? "") || string.IsNullOrWhiteSpace(id)))
                throw new InvalidOperationException("Robinhood returned an equity watchlist member without a valid symbol or instrument identity.");
            if (id is not null && !identities.Add((type, id)))
                throw new InvalidOperationException("Robinhood returned duplicate watchlist members; complete retrieval cannot be confirmed.");
            members.Add(new(type, symbol, type == "instrument" ? id : null));
        }
        return new(watchlist, members);
    }

    internal static EquityResolution ResolveEquity(JsonElement root, string requestedSymbol)
    {
        string symbol = NormalizeSymbol(requestedSymbol);
        JsonElement rows = Array(root, "results");
        JsonElement[] matches = rows.EnumerateArray().Where(row => NormalizeSymbol(Text(row, "symbol")) == symbol).ToArray();
        if (matches.Length == 0)
            return new(requestedSymbol, symbol, null, null, false, "No exact stock or equity ETF ticker matched the instrument catalog.");
        if (matches.Length != 1)
            return new(requestedSymbol, symbol, null, null, false, "The provider returned ambiguous instrument identities for this ticker.");
        JsonElement match = matches[0];
        string? id = Text(match, "instrument_id");
        if (string.IsNullOrWhiteSpace(id))
            return new(requestedSymbol, symbol, null, null, false, "The provider did not return a stable equity instrument identity.");
        // The request is explicitly asset_type=instrument. Trading permission is unrelated to archival eligibility.
        string name = Text(match, "simple_name") ?? Text(match, "name") ?? symbol;
        return new(requestedSymbol, symbol, name, id, true);
    }

    internal static HistoricalDownload ParseHistory(JsonElement root, HistoricalDataRequest request, DateTimeOffset fetchedAtUtc)
    {
        ValidateRequest(request);
        string symbol = NormalizeSymbol(request.Symbol);
        JsonElement results = Array(root, "results");
        EnsureComplete(root, results.GetArrayLength(), checkCounts: false);
        JsonElement[] matches = results.EnumerateArray().Where(row => NormalizeSymbol(Text(row, "symbol")) == symbol).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Robinhood returned no unique historical result for the requested equity.");
        JsonElement result = matches[0];
        if (RequiredText(result, "interval") != IntervalName(request.SourceIntervalSeconds))
            throw new InvalidOperationException("Robinhood returned a different source interval than requested.");
        if (Text(result, "bounds") is { } bounds && bounds != request.SessionBounds)
            throw new InvalidOperationException("Robinhood returned different session bounds than requested.");
        if (Text(result, "adjustment_type") is { } adjustment && adjustment != request.AdjustmentPolicy)
            throw new InvalidOperationException("Robinhood returned a different adjustment policy than requested.");
        string? returnedId = Text(result, "instrument_id");
        if (returnedId is not null && request.InstrumentId is not null && returnedId != request.InstrumentId)
            throw new InvalidOperationException("Robinhood returned a different instrument identity than requested.");
        string instrumentId = returnedId ?? request.InstrumentId ?? throw new InvalidOperationException("An equity instrument identity must be resolved before saving history.");
        var candles = new SortedDictionary<DateTimeOffset, HistoricalCandle>();
        if (!result.TryGetProperty("bars", out JsonElement bars) || bars.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidOperationException("Robinhood returned malformed historical bars.");
        if (bars.ValueKind == JsonValueKind.Array)
        foreach (JsonElement bar in bars.EnumerateArray())
        {
            if (bar.ValueKind == JsonValueKind.Null) continue;
            if (bar.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Robinhood returned a malformed candle.");
            if (bar.TryGetProperty("interpolated", out var interpolated))
            {
                if (interpolated.ValueKind == JsonValueKind.True) continue;
                if (interpolated.ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("Robinhood returned an invalid interpolation flag.");
            }
            DateTimeOffset start = DateTimeOffset.TryParse(RequiredText(bar, "begins_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp.ToUniversalTime()
                : throw new InvalidOperationException("Robinhood returned an invalid candle timestamp.");
            DateTimeOffset end = start.AddSeconds(request.SourceIntervalSeconds);
            if (start < request.FromUtc || end > request.ThroughUtc || end > fetchedAtUtc) continue;
            if (start.UtcTicks % TimeSpan.FromSeconds(request.SourceIntervalSeconds).Ticks != 0)
                throw new InvalidOperationException("Robinhood returned an unaligned historical candle.");
            // Missing numeric values remain placeholders until the library can apply
            // them against saved candles. They must never erase existing values.
            decimal open = OptionalDecimal(bar, "open_price") ?? 0m, high = OptionalDecimal(bar, "high_price") ?? 0m,
                low = OptionalDecimal(bar, "low_price") ?? 0m, close = OptionalDecimal(bar, "close_price") ?? 0m;
            decimal? volume = OptionalDecimal(bar, "volume");
            bool completePrices = open > 0 && high > 0 && low > 0 && close > 0;
            if (open < 0 || high < 0 || low < 0 || close < 0 || volume < 0 ||
                (completePrices && (high < Math.Max(open, close) || low > Math.Min(open, close) || high < low)))
                throw new InvalidOperationException("Robinhood returned invalid candle prices or volume.");
            var candle = new HistoricalCandle(start, end, end, open, high, low, close, volume);
            if (candles.TryGetValue(start, out var previous) && previous != candle)
                throw new InvalidOperationException("Robinhood returned conflicting candles at the same timestamp.");
            candles[start] = candle;
        }
        string basis = Text(result, "adjustment_revision") is { } revision
            ? "robinhood-split-revision:" + revision : "robinhood-split-unversioned";
        return new("Robinhood", instrumentId, symbol, request.SourceIntervalSeconds, request.AdjustmentPolicy, basis,
            request.SessionBounds, fetchedAtUtc.ToUniversalTime(), request.FromUtc.ToUniversalTime(), request.ThroughUtc.ToUniversalTime(), [.. candles.Values]);
    }

    private static JsonElement Array(JsonElement root, string field)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Robinhood returned an unexpected library response.");
        return array;
    }

    private static void EnsureComplete(JsonElement node, int returnedCount, bool checkCounts = true)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (property.Name is "next" or "next_cursor" or "next_page_token" or "next_page" or "has_more")
            {
                bool empty = property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.False ||
                    property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString());
                if (!empty) throw new InvalidOperationException("Robinhood returned a paginated response. Complete retrieval is unavailable through the current read-only tool.");
            }
            if (checkCounts && property.Name is "count" or "total_count" or "total")
            {
                if (!property.Value.TryGetInt32(out int expected) || expected != returnedCount)
                    throw new InvalidOperationException("Robinhood response count does not match the returned collection; retrieval is incomplete.");
            }
            if (property.Value.ValueKind == JsonValueKind.Object) EnsureComplete(property.Value, returnedCount, checkCounts);
        }
    }

    private static string? Text(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(node.GetString())
        ? node.GetString()!.Trim() : null;

    private static string RequiredText(JsonElement item, string property) => Text(item, property)
        ?? throw new InvalidOperationException($"Robinhood returned no valid {property} value.");

    private static decimal? OptionalDecimal(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var node) || node.ValueKind == JsonValueKind.Null ||
            (node.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(node.GetString()))) return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetDecimal(out decimal number)) return number;
        if (node.ValueKind == JsonValueKind.String && decimal.TryParse(node.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidOperationException($"Robinhood returned no valid {property} value.");
    }
}
