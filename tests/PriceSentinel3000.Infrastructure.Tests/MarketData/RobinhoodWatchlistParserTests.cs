using System.Text.Json;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodWatchlistParserTests
{
    [Fact]
    public void ParseListId_ReturnsExactDisplayNameMatch()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "data": {
                "lists": [
                  { "id": "popular", "display_name": "100 Most Popular" },
                  { "id": "24-hour", "display_name": "24 Hour Market" }
                ]
              }
            }
            """);

        string id = RobinhoodWatchlistParser.ParseListId(
            document.RootElement,
            "24 Hour Market");

        Assert.Equal("24-hour", id);
    }

    [Fact]
    public void ParseListId_RequiresExactDisplayName()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "data": { "lists": [{ "id": "24-hour", "display_name": "24 hour market" }] } }""");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RobinhoodWatchlistParser.ParseListId(
                document.RootElement,
                "24 Hour Market"));

        Assert.Contains("no watchlist named", exception.Message);
    }

    [Fact]
    public void ParseListId_MatchingListWithoutId_Throws()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "data": { "lists": [{ "display_name": "24 Hour Market" }] } }""");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RobinhoodWatchlistParser.ParseListId(
                document.RootElement,
                "24 Hour Market"));

        Assert.Contains("no id", exception.Message);
    }

    [Fact]
    public void ParseInstrumentSymbols_ReturnsCanonicalDeduplicatedInstruments()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "data": {
                "items": [
                  { "object_type": "instrument", "symbol": " intc " },
                  { "object_type": "instrument", "symbol": "INTC" },
                  { "object_type": "INSTRUMENT", "symbol": "amd" },
                  { "object_type": "currency_pair", "symbol": "BTC-USD" },
                  { "object_type": "instrument" },
                  { "object_type": "instrument", "symbol": 42 },
                  null
                ]
              }
            }
            """);

        IReadOnlySet<string> symbols =
            RobinhoodWatchlistParser.ParseInstrumentSymbols(document.RootElement);

        Assert.Equal(2, symbols.Count);
        Assert.Contains("INTC", symbols);
        Assert.Contains("AMD", symbols);
    }

    [Fact]
    public void ParseInstrumentSymbols_EmptyItems_ReturnsEmptySet()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "data": { "items": [] } }""");

        IReadOnlySet<string> symbols =
            RobinhoodWatchlistParser.ParseInstrumentSymbols(document.RootElement);

        Assert.Empty(symbols);
    }

    [Theory]
    [InlineData("{}", "lists")]
    [InlineData("{ \"data\": {} }", "lists")]
    [InlineData("{ \"data\": { \"lists\": null } }", "lists")]
    [InlineData("{}", "items")]
    [InlineData("{ \"data\": {} }", "items")]
    [InlineData("{ \"data\": { \"items\": null } }", "items")]
    public void Parse_UnexpectedPayload_Throws(string json, string member)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        InvalidOperationException exception = member == "lists"
            ? Assert.Throws<InvalidOperationException>(() =>
                RobinhoodWatchlistParser.ParseListId(
                    document.RootElement,
                    "24 Hour Market"))
            : Assert.Throws<InvalidOperationException>(() =>
                RobinhoodWatchlistParser.ParseInstrumentSymbols(
                    document.RootElement));

        Assert.Contains("watchlist response", exception.Message);
    }
}
