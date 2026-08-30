using System.Text.Json;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodInstrumentSearchParserTests
{
    [Fact]
    public void Parse_ReturnsRobinhoodSymbolsAndDisplayNames()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "data": {
                "results": [
                  {
                    "instrument_id": "one",
                    "symbol": "AMD",
                    "name": "Advanced Micro Devices, Inc. Common Stock",
                    "simple_name": "Advanced Micro Devices"
                  },
                  {
                    "instrument_id": "two",
                    "symbol": "AMDL",
                    "name": "GraniteShares 2x Long AMD Daily ETF",
                    "simple_name": "GraniteShares 2x Long AMD Daily ETF"
                  }
                ]
              }
            }
            """);

        var results = RobinhoodInstrumentSearchParser.Parse(document.RootElement);

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("AMD", result.Symbol);
                Assert.Equal("Advanced Micro Devices", result.Name);
            },
            result =>
            {
                Assert.Equal("AMDL", result.Symbol);
                Assert.Equal("GraniteShares 2x Long AMD Daily ETF", result.Name);
            });
    }

    [Fact]
    public void Parse_SkipsMalformedAndDuplicateRows()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "data": {
                "results": [
                  null,
                  { "name": "Missing symbol" },
                  { "symbol": "amd", "name": "Advanced Micro Devices" },
                  { "symbol": "AMD", "name": "Duplicate" },
                  { "symbol": "AM", "name": "" }
                ]
              }
            }
            """);

        var results = RobinhoodInstrumentSearchParser.Parse(document.RootElement);

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("AMD", result.Symbol);
                Assert.Equal("Advanced Micro Devices", result.Name);
            },
            result =>
            {
                Assert.Equal("AM", result.Symbol);
                Assert.Equal("AM", result.Name);
            });
    }

    [Fact]
    public void Parse_EmptyResults_ReturnsNoSuggestions()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{ "data": { "results": [] } }""");

        var results = RobinhoodInstrumentSearchParser.Parse(document.RootElement);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"data\": {} }")]
    [InlineData("{ \"data\": { \"results\": null } }")]
    public void Parse_UnexpectedPayload_Throws(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RobinhoodInstrumentSearchParser.Parse(document.RootElement));

        Assert.Contains("instrument-search", exception.Message);
    }
}
