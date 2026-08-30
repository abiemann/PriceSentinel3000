using System.Text.Json;

namespace PriceSentinel3000.Infrastructure.MarketData;

internal static class RobinhoodWatchlistParser
{
    public static string ParseListId(JsonElement root, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        JsonElement lists = RequiredArray(root, "lists");
        foreach (JsonElement list in lists.EnumerateArray())
        {
            if (list.ValueKind is not JsonValueKind.Object ||
                !string.Equals(
                    String(list, "display_name"),
                    displayName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string id = String(list, "id").Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    $"Robinhood returned no id for watchlist '{displayName}'.");
            }

            return id;
        }

        throw new InvalidOperationException(
            $"Robinhood returned no watchlist named '{displayName}'.");
    }

    public static IReadOnlySet<string> ParseInstrumentSymbols(JsonElement root)
    {
        JsonElement items = RequiredArray(root, "items");
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object ||
                !string.Equals(
                    String(item, "object_type"),
                    "instrument",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string symbol = String(item, "symbol").Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    private static JsonElement RequiredArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind is not JsonValueKind.Object ||
            !data.TryGetProperty(propertyName, out JsonElement array) ||
            array.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Robinhood returned an unexpected watchlist response.");
        }

        return array;
    }

    private static string String(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement node) &&
        node.ValueKind is JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
}
