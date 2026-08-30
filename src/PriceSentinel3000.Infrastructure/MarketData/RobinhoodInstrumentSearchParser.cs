using System.Text.Json;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Infrastructure.MarketData;

internal static class RobinhoodInstrumentSearchParser
{
    public static IReadOnlyList<InstrumentSearchResult> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind is not JsonValueKind.Object ||
            !data.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Robinhood returned an unexpected instrument-search response.");
        }

        var suggestions = new List<InstrumentSearchResult>();
        var seenSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement result in results.EnumerateArray())
        {
            if (result.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            string symbol = String(result, "symbol").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol) || !seenSymbols.Add(symbol))
            {
                continue;
            }

            string simpleName = String(result, "simple_name");
            string name = !string.IsNullOrWhiteSpace(simpleName) &&
                          !string.Equals(
                              simpleName.Trim(),
                              symbol,
                              StringComparison.OrdinalIgnoreCase)
                ? simpleName.Trim()
                : FirstNonEmpty(String(result, "name"), symbol);
            suggestions.Add(new(symbol, name));
        }

        return suggestions;
    }

    private static string String(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement node) &&
        node.ValueKind is JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value)).Trim();
}
