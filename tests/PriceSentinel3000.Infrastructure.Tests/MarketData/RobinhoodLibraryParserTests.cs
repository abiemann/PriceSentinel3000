using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodLibraryParserTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-04T13:30:00Z");
    private static HistoricalDataRequest Request(int seconds = 15) => new("TEST", Start, Start.AddMinutes(1), seconds, "regular", "split", "public-instrument-id");
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Watchlists_UsesPersonalContainerAndPreservesDuplicateNamesWithDistinctIds()
    {
        var result = RobinhoodLibraryParser.ParseWatchlists(Parse("""
            {"data":{"count":2,"watchlists":[
              {"id":"first","display_name":"My equities","item_count":2},
              {"id":"second","display_name":"My equities","item_count":0}],"next":null}}
            """));
        Assert.Equal(2, result.Count);
        Assert.Equal(new PersonalWatchlist("first", "My equities", 2), result[0]);
        Assert.NotEqual(result[0].Id, result[1].Id);
    }

    [Theory]
    [InlineData("{\"data\":{\"lists\":[]}}")]
    [InlineData("{\"data\":{\"watchlists\":[],\"count\":1}}")]
    [InlineData("{\"data\":{\"watchlists\":[],\"next_cursor\":\"more\"}}")]
    [InlineData("{\"data\":{\"watchlists\":[],\"pagination\":{\"has_more\":true}}}")]
    [InlineData("{\"data\":{\"watchlists\":[{\"id\":\"x\",\"display_name\":\"Mine\"}]}}")]
    public void Watchlists_RejectsMalformedOrPartialCollections(string response) =>
        Assert.Throws<InvalidOperationException>(() => RobinhoodLibraryParser.ParseWatchlists(Parse(response)));

    [Fact]
    public void Members_CountsAllTypesAndPreservesEquityIdentityForPreview()
    {
        var result = RobinhoodLibraryParser.ParseWatchlistMembers(Parse("""
            {"data":{"items":[
              {"object_type":"instrument","object_id":"equity-id","symbol":"test"},
              {"object_type":"currency_pair","object_id":"crypto-id","symbol":"BTC-USD"}],"next":null}}
            """), new("list-id", "Mine", 2));
        Assert.Equal(2, result.Members.Count);
        Assert.Equal(new PersonalWatchlistMember("instrument", "TEST", "equity-id"), result.Members[0]);
        Assert.Equal("currency_pair", result.Members[1].ObjectType);
        Assert.Null(result.Members[1].ProviderInstrumentId);
    }

    [Theory]
    [InlineData("{\"data\":{\"items\":[]}}")]
    [InlineData("{\"data\":{\"items\":[{\"object_type\":\"instrument\",\"symbol\":\"TEST\"}]}}")]
    [InlineData("{\"data\":{\"items\":[{\"object_type\":\"instrument\",\"symbol\":\"TEST\",\"object_id\":\"one\"}],\"next_page_token\":\"more\"}}")]
    public void Members_RejectsIncompleteMembershipOrMissingIdentity(string response) =>
        Assert.Throws<InvalidOperationException>(() => RobinhoodLibraryParser.ParseWatchlistMembers(Parse(response), new("id", "Mine", 1)));

    [Fact]
    public void EquityResolution_RequiresExactCatalogMatchWithoutTradingEligibility()
    {
        var result = RobinhoodLibraryParser.ResolveEquity(Parse("""
            {"data":{"results":[
              {"symbol":"TESTL","instrument_id":"other","name":"Other ETF"},
              {"symbol":"TEST","instrument_id":"id","simple_name":"Test Company","tradeable":false}]}}
            """), " test ");
        Assert.True(result.IsSupported);
        Assert.Equal("TEST", result.Symbol);
        Assert.Equal("Test Company", result.CompanyName);
        Assert.Equal("id", result.ProviderInstrumentId);
    }

    [Theory]
    [InlineData("{\"data\":{\"results\":[]}}")]
    [InlineData("{\"data\":{\"results\":[{\"symbol\":\"TESTL\",\"instrument_id\":\"id\"}]}}")]
    [InlineData("{\"data\":{\"results\":[{\"symbol\":\"TEST\"}]}}")]
    [InlineData("{\"data\":{\"results\":[{\"symbol\":\"TEST\",\"instrument_id\":\"id1\"},{\"symbol\":\"TEST\",\"instrument_id\":\"id2\"}]}}")]
    public void EquityResolution_DoesNotGuessIdentity(string response) =>
        Assert.False(RobinhoodLibraryParser.ResolveEquity(Parse(response), "TEST").IsSupported);

    [Fact]
    public void History_RetainsExactDecimalOhlcAndNullVolumeWithoutQuotes()
    {
        HistoricalDownload result = RobinhoodLibraryParser.ParseHistory(History("15second", Candle(Start)), Request(), Start.AddDays(1));
        HistoricalCandle candle = Assert.Single(result.Candles);
        Assert.Equal(12.123456789012345678901234567m, candle.Open);
        Assert.Null(candle.Volume);
        Assert.Equal(Start.AddSeconds(15), candle.AvailableAtUtc);
        Assert.Equal("robinhood-split-unversioned", result.AdjustmentBasis);
        Assert.Equal("public-instrument-id", result.InstrumentId);
        Assert.Equal(Start, result.RequestedFromUtc);
        Assert.Equal(Start.AddMinutes(1), result.RequestedThroughUtc);
    }

    [Fact]
    public void History_MissingInterpolatedOutsideAndUnfinishedBarsRemainGaps()
    {
        var root = History("15second", Candle(Start.AddSeconds(-15)), Candle(Start),
            Candle(Start.AddSeconds(15)).Replace("\"session\":\"reg\"", "\"interpolated\":true"),
            "null", Candle(Start.AddSeconds(45)), Candle(Start.AddMinutes(1)));
        var result = RobinhoodLibraryParser.ParseHistory(root, Request(), Start.AddSeconds(40));
        Assert.Single(result.Candles);
        Assert.Equal(Start, result.Candles[0].StartsAtUtc);
    }

    [Theory]
    [InlineData("\"open_price\":\"12.123456789012345678901234567\"", "\"open_price\":-1")]
    [InlineData("\"open_price\":\"12.123456789012345678901234567\"", "\"open_price\":\"unknown\"")]
    [InlineData("\"open_price\":\"12.123456789012345678901234567\"", "\"open_price\":true")]
    [InlineData("\"high_price\":\"13\"", "\"high_price\":-1")]
    [InlineData("\"high_price\":\"13\"", "\"high_price\":\"10\"")]
    [InlineData("\"volume\":null", "\"volume\":-1")]
    public void History_RejectsInvalidOhlcvInsteadOfSubstitutingClose(string before, string after)
    {
        string bar = Candle(Start).Replace(before, after);
        Assert.Throws<InvalidOperationException>(() => RobinhoodLibraryParser.ParseHistory(History("15second", bar), Request(), Start.AddDays(1)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData(null)]
    public void History_RetainsUnavailablePricesAsPlaceholdersForSavedValueFallback(string? value)
    {
        foreach (string field in new[] { "open_price", "high_price", "low_price", "close_price" })
        {
            var bar = System.Text.Json.Nodes.JsonNode.Parse(Candle(Start))!.AsObject();
            if (value is null) bar.Remove(field);
            else bar[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
            HistoricalDownload result = RobinhoodLibraryParser.ParseHistory(
                History("15second", bar.ToJsonString()), Request(), Start.AddDays(1));
            HistoricalCandle candle = Assert.Single(result.Candles);
            Assert.Equal(0m, field switch
            {
                "open_price" => candle.Open, "high_price" => candle.High,
                "low_price" => candle.Low, _ => candle.Close,
            });
            Assert.Equal(Start, candle.StartsAtUtc);
        }
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void History_EmptyVolumeRemainsUnknown(string value)
    {
        HistoricalDownload result = RobinhoodLibraryParser.ParseHistory(
            History("15second", Candle(Start).Replace("\"volume\":null", "\"volume\":" + value)),
            Request(), Start.AddDays(1));
        Assert.Null(Assert.Single(result.Candles).Volume);
    }

    [Fact]
    public void History_RejectsWrongResolutionAndConflictingDuplicateCandles()
    {
        Assert.Throws<InvalidOperationException>(() => RobinhoodLibraryParser.ParseHistory(History("minute", Candle(Start)), Request(), Start.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => RobinhoodLibraryParser.ParseHistory(History("15second", Candle(Start),
            Candle(Start).Replace("\"close_price\":\"12.5\"", "\"close_price\":\"12.6\"")), Request(), Start.AddDays(1)));
    }

    [Fact]
    public void History_RejectsMixedIdentityBoundsAndAdjustment()
    {
        foreach (string metadata in new[] { "\"bounds\":\"extended\"", "\"adjustment_type\":\"none\"", "\"instrument_id\":\"different\"" })
        {
            var root = Parse("{\"data\":{\"results\":[{\"symbol\":\"TEST\",\"interval\":\"15second\","+metadata+",\"bars\":[]}]}}");
            Assert.Throws<InvalidOperationException>(() => RobinhoodLibraryParser.ParseHistory(root, Request(), Start.AddDays(1)));
        }
    }

    [Theory]
    [InlineData(15, "15second")]
    [InlineData(30, "30second")]
    [InlineData(60, "minute")]
    public void Request_UsesExactIntervalAndSuppliedBoundsWithoutFallback(int seconds, string name)
    {
        var request = Request(seconds) with { SessionBounds = "extended", Symbol = " test " };
        var arguments = RobinhoodMcpGateway.BuildLibraryHistoryArguments(request);
        Assert.Equal(name, arguments["interval"]);
        Assert.Equal("extended", arguments["bounds"]);
        Assert.Equal("split", arguments["adjustment_type"]);
        Assert.Equal(request.FromUtc.ToString("O"), arguments["start_time"]);
        Assert.Equal(request.ThroughUtc.ToString("O"), arguments["end_time"]);
        Assert.Equal(new[] { "TEST" }, Assert.IsType<string[]>(arguments["symbols"]));
    }

    [Fact]
    public void Request_RejectsUnsupportedIntervalAndInvalidRangeBeforeNetwork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RobinhoodMcpGateway.BuildLibraryHistoryArguments(Request(120)));
        Assert.Throws<ArgumentException>(() => RobinhoodMcpGateway.BuildLibraryHistoryArguments(Request() with { ThroughUtc = Start }));
    }

    [Fact]
    public void History_TwentyFourFiveRetainsOvernightCandlesAndBounds()
    {
        DateTimeOffset from = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        var request = Request() with { SessionBounds = "24_5", FromUtc = from, ThroughUtc = from.AddMinutes(1) };
        var root = Parse("{\"data\":{\"results\":[{\"symbol\":\"TEST\",\"interval\":\"15second\",\"bounds\":\"24_5\",\"bars\":[" + Candle(from) + "]}]}}");

        HistoricalDownload result = RobinhoodLibraryParser.ParseHistory(root, request, from.AddMinutes(2));
        Assert.Equal(from, Assert.Single(result.Candles).StartsAtUtc);
        Assert.Equal("24_5", result.SessionBounds);
        Assert.Equal("24_5", RobinhoodMcpGateway.BuildLibraryHistoryArguments(request)["bounds"]);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("overnight")]
    public void Request_RejectsUnsupportedSessionBoundsBeforeNetwork(string bounds) =>
        Assert.Throws<ArgumentException>(() => RobinhoodMcpGateway.BuildLibraryHistoryArguments(Request() with { SessionBounds = bounds }));

    [Fact]
    public async Task DisconnectedLibraryRequest_DoesNotAttemptAuthentication()
    {
        await using var gateway = RobinhoodMcpGateway.CreateDefault();
        Assert.False(gateway.HasActiveConnection);
        var error = await Assert.ThrowsAsync<MarketDataConnectionUnavailableException>(() => gateway.GetWatchlistsAsync(CancellationToken.None));
        Assert.Contains("Connect to Robinhood", error.Message);
        Assert.False(gateway.HasActiveConnection);
    }

    private static JsonElement History(string interval, params string[] bars) => Parse(
        "{\"data\":{\"results\":[{\"symbol\":\"TEST\",\"interval\":\""+interval+"\",\"bounds\":\"regular\",\"bars\":["+string.Join(',', bars)+"]}]}}");

    private static string Candle(DateTimeOffset start) => "{\"begins_at\":\"" + start.ToString("O") +
        "\",\"open_price\":\"12.123456789012345678901234567\",\"high_price\":\"13\",\"low_price\":\"12\",\"close_price\":\"12.5\",\"volume\":null,\"session\":\"reg\"}";
}
