using System.Globalization;
using System.Text.Json;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Infrastructure.MarketData;

internal static class RobinhoodMarketDataParser
{
    public static MarketQuote ParseQuote(
        JsonElement root,
        Instrument instrument,
        DateTimeOffset observedAtUtc)
    {
        JsonElement result = FindResult(root, instrument.Symbol);

        if (!result.TryGetProperty("quote", out JsonElement quote) ||
            quote.ValueKind is JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Robinhood returned no quote for {instrument.Symbol}.");
        }

        if (!quote.GetProperty("has_traded").GetBoolean())
        {
            throw new InvalidOperationException(
                $"Robinhood reports that {instrument.Symbol} has never traded.");
        }

        string state = quote.GetProperty("state").GetString() ?? string.Empty;

        if (!string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Robinhood reports {instrument.Symbol} in state '{state}'.");
        }

        decimal last = ParseDecimal(quote, "last_trade_price");
        DateTimeOffset sourceTimestamp = ParseTimestamp(
            quote,
            "venue_last_trade_time");

        if (TryReadNonRegularTrade(quote, out decimal nonRegularLast,
                out DateTimeOffset nonRegularTimestamp) &&
            nonRegularTimestamp > sourceTimestamp)
        {
            last = nonRegularLast;
            sourceTimestamp = nonRegularTimestamp;
        }

        return new(
            instrument,
            observedAtUtc.ToUniversalTime(),
            sourceTimestamp,
            ParseDecimal(quote, "bid_price"),
            ParseDecimal(quote, "ask_price"),
            last,
            0m);
    }

    public static IReadOnlyList<MarketQuote> ParseHistory(
        JsonElement root,
        Instrument instrument,
        DateTimeOffset observedAtUtc)
    {
        JsonElement result = FindResult(root, instrument.Symbol);

        if (!result.TryGetProperty("bars", out JsonElement bars) ||
            bars.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var quotes = new List<MarketQuote>();

        foreach (JsonElement bar in bars.EnumerateArray())
        {
            if (bar.ValueKind is JsonValueKind.Null ||
                bar.TryGetProperty("interpolated", out JsonElement interpolated) &&
                interpolated.ValueKind is JsonValueKind.True)
            {
                continue;
            }

            decimal close = ParseDecimal(bar, "close_price");
            decimal open = ParseDecimalOrDefault(bar, "open_price", close);
            decimal high = ParseDecimalOrDefault(bar, "high_price", close);
            decimal low = ParseDecimalOrDefault(bar, "low_price", close);
            decimal volume = bar.TryGetProperty("volume", out JsonElement volumeNode)
                ? volumeNode.GetDecimal()
                : 0m;
            quotes.Add(new(
                instrument,
                observedAtUtc.ToUniversalTime(),
                ParseTimestamp(bar, "begins_at"),
                0m,
                0m,
                close,
                volume,
                open,
                high,
                low,
                close));
        }

        return quotes
            .OrderBy(quote => quote.SourceTimestampUtc)
            .DistinctBy(quote => quote.SourceTimestampUtc)
            .ToArray();
    }

    private static JsonElement FindResult(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Robinhood returned an unexpected market-data response.");
        }

        foreach (JsonElement result in results.EnumerateArray())
        {
            if (result.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            string? resultSymbol = result.TryGetProperty("symbol", out JsonElement symbolNode)
                ? symbolNode.GetString()
                : result.TryGetProperty("quote", out JsonElement quote) &&
                  quote.ValueKind is JsonValueKind.Object &&
                  quote.TryGetProperty("symbol", out JsonElement quoteSymbol)
                    ? quoteSymbol.GetString()
                    : null;

            if (string.Equals(resultSymbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            $"Robinhood could not resolve equity symbol {symbol}.");
    }

    private static bool TryReadNonRegularTrade(
        JsonElement quote,
        out decimal price,
        out DateTimeOffset timestamp)
    {
        price = 0m;
        timestamp = default;

        return quote.TryGetProperty("last_non_reg_trade_price", out JsonElement priceNode) &&
               priceNode.ValueKind is JsonValueKind.String &&
               decimal.TryParse(priceNode.GetString(), NumberStyles.Number,
                   CultureInfo.InvariantCulture, out price) &&
               quote.TryGetProperty("venue_last_non_reg_trade_time", out JsonElement timeNode) &&
               timeNode.ValueKind is JsonValueKind.String &&
               DateTimeOffset.TryParse(timeNode.GetString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal, out timestamp);
    }

    private static decimal ParseDecimal(JsonElement element, string propertyName)
    {
        string? value = element.GetProperty(propertyName).GetString();

        if (!decimal.TryParse(value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal parsed))
        {
            throw new InvalidOperationException(
                $"Robinhood returned an invalid {propertyName} value.");
        }

        return parsed;
    }

    private static decimal ParseDecimalOrDefault(
        JsonElement element,
        string propertyName,
        decimal defaultValue)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement node))
        {
            return defaultValue;
        }

        string? value = node.ValueKind is JsonValueKind.String
            ? node.GetString()
            : node.GetRawText();
        return decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal parsed)
            ? parsed
            : defaultValue;
    }

    private static DateTimeOffset ParseTimestamp(
        JsonElement element,
        string propertyName)
    {
        string? value = element.GetProperty(propertyName).GetString();

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            throw new InvalidOperationException(
                $"Robinhood returned an invalid {propertyName} value.");
        }

        return parsed.ToUniversalTime();
    }
}
