using System.Text.Json;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodBrokerParserTests
{
    [Fact]
    public void ParseAgenticAccount_SelectsOnlyActiveAgenticAccount()
    {
        JsonElement root = Json(
            """
            {"data":{"accounts":[
              {"account_number":"11110000","agentic_allowed":false,"deactivated":false,"permanently_deactivated":false,"state":"active","brokerage_account_type":"margin"},
              {"account_number":"22224242","agentic_allowed":true,"deactivated":false,"permanently_deactivated":false,"state":"active","brokerage_account_type":"cash"}
            ]}}
            """);

        BrokerAccount account = RobinhoodBrokerParser.ParseAgenticAccount(root);

        Assert.Equal("22224242", account.AccountNumber);
        Assert.Equal("????4242", account.MaskedNumber);
        Assert.True(account.AgenticAllowed);
        Assert.True(account.IsActive);
    }

    [Fact]
    public void ParsePortfolio_UsesBrokerBuyingPowerObject()
    {
        BrokerPortfolio portfolio = RobinhoodBrokerParser.ParsePortfolio(Json(
            """
            {"data":{"total_value":"1500.25","equity_value":"1200.25","cash":"300.00","currency":"USD","buying_power":{"buying_power":"287.50"}}}
            """));

        Assert.Equal(1_500.25m, portfolio.TotalValue);
        Assert.Equal(287.50m, portfolio.BuyingPower);
    }

    [Fact]
    public void ParsePosition_UsesSharesAvailableForSells()
    {
        BrokerPosition position = RobinhoodBrokerParser.ParsePosition(Json(
            """
            {"data":{"positions":[{"symbol":"SOFI","quantity":"4.5","average_buy_price":"10.25","shares_available_for_sells":"3.5","shares_held_for_sells":"1"}]}}
            """), "SOFI");

        Assert.Equal(4.5m, position.Quantity);
        Assert.Equal(3.5m, position.SharesAvailableForSells);
        Assert.Equal(1m, position.SharesHeldForSells);
    }

    [Fact]
    public void ParsePosition_TreatsAShortPositionAsAnExistingPosition()
    {
        BrokerPosition position = RobinhoodBrokerParser.ParsePosition(Json(
            """
            {"data":{"positions":[{"symbol":"SOFI","quantity":"-2","average_buy_price":"10.25","shares_available_for_sells":"0","shares_held_for_sells":"0"}]}}
            """), "SOFI");

        Assert.True(position.HasPosition);
    }

    [Fact]
    public void ParseTradability_RequiresMatchingAccountTypeAndExactFractionalState()
    {
        JsonElement root = Json(
            """
            {"data":{"results":[{"symbol":"SOFI","state":"active","tradeable":true,"fractional_tradability":"not_tradable","account_type_tradabilities":[{"account_type":"individual","account_type_tradability":"position_closing_only"},{"account_type":"ira_roth","account_type_tradability":"tradable"}]}]}}
            """);

        EquityTradability closingOnly = RobinhoodBrokerParser.ParseTradability(
            root,
            "SOFI",
            "individual");
        EquityTradability ira = RobinhoodBrokerParser.ParseTradability(
            root,
            "SOFI",
            "ira_roth");

        Assert.False(closingOnly.Tradeable);
        Assert.False(closingOnly.FractionalTradeable);
        Assert.True(ira.Tradeable);
    }

    [Fact]
    public void ParseTradability_UsesConfirmedSessionCapabilityFields()
    {
        EquityTradability tradability = RobinhoodBrokerParser.ParseTradability(Json(
            """
            {"data":{"results":[{"symbol":"AMD","state":"active","tradeable":true,"all_day_tradability":"all_day_tradability_tradable","twenty_four_seven_tradability":"twenty_four_seven_tradability_tradable"}]}}
            """), "AMD", "individual");

        Assert.True(tradability.ExtendedHoursTradeable);
        Assert.True(tradability.OvernightTradeable);
    }

    [Fact]
    public void ParseTradability_RequiresExactSessionCapabilitySentinels()
    {
        EquityTradability tradability = RobinhoodBrokerParser.ParseTradability(Json(
            """
            {"data":{"results":[{"symbol":"AMD","state":"active","tradeable":true,"all_day_tradability":"tradable","twenty_four_seven_tradability":"tradable"}]}}
            """), "AMD", "individual");

        Assert.False(tradability.ExtendedHoursTradeable);
        Assert.False(tradability.OvernightTradeable);
    }

    [Fact]
    public void ParseTradability_DoesNotTreatExtendedHoursFieldAsOvernightEligibility()
    {
        EquityTradability tradability = RobinhoodBrokerParser.ParseTradability(Json(
            """
            {"data":{"results":[{"symbol":"AMD","state":"active","tradeable":true,"all_day_tradability":"all_day_tradability_tradable"}]}}
            """), "AMD", "individual");

        Assert.True(tradability.ExtendedHoursTradeable);
        Assert.False(tradability.OvernightTradeable);
    }

    [Fact]
    public void ParseTradability_PreservesCapabilitiesWhenHeadlineIsNotTradeable()
    {
        EquityTradability tradability = RobinhoodBrokerParser.ParseTradability(Json(
            """
            {"data":{"results":[{"symbol":"AMD","state":"active","tradeable":false,"all_day_tradability":"all_day_tradability_tradable","twenty_four_seven_tradability":"twenty_four_seven_tradability_tradable"}]}}
            """), "AMD", "individual");

        Assert.False(tradability.Tradeable);
        Assert.True(tradability.ExtendedHoursTradeable);
        Assert.True(tradability.OvernightTradeable);
    }

    [Fact]
    public void ParseReview_FailsClosedWhenCanPlaceIsFalse()
    {
        BrokerOrderIntent intent = Intent();
        BrokerOrderReview review = RobinhoodBrokerParser.ParseReview(Json(
            """
            {"data":{"order_checks":{"can_place":false,"sufficient_funds":{"passed":true}},"quote_data":{"bid_price":"10.00","ask_price":"10.02","last_trade_price":"10.01"}}}
            """), intent);

        Assert.False(review.Accepted);
        Assert.Contains(review.Blockers, blocker => blocker.Contains("can_place"));
        Assert.Equal(10.02m, review.AskPrice);
    }

    [Fact]
    public void ParseReview_BlocksEveryNonEmptyRobinhoodAlert()
    {
        BrokerOrderReview review = RobinhoodBrokerParser.ParseReview(Json(
            """
            {"data":{"order_checks":{"alert_type":"VOLATILE_STOCK"},"market_data_disclosure":"Bid $10.00 · Ask $10.02 · Last $10.01.","quote_data":{"bid_price":"10.00","ask_price":"10.02","last_trade_price":"10.01"}}}
            """), Intent());

        Assert.False(review.Accepted);
        Assert.Single(review.Blockers);
        Assert.Contains("VOLATILE_STOCK", review.Blockers[0]);
        Assert.Equal("Bid $10.00 · Ask $10.02 · Last $10.01.", review.MarketDataDisclosure);
    }

    [Fact]
    public void ParseReview_AcceptsOnlyAnExplicitEmptyOrderChecksObject()
    {
        BrokerOrderReview accepted = RobinhoodBrokerParser.ParseReview(Json(
            """
            {"data":{"order_checks":{},"quote_data":{"bid_price":"10.00","ask_price":"10.02","last_trade_price":"10.01"}}}
            """), Intent());
        BrokerOrderReview missing = RobinhoodBrokerParser.ParseReview(Json(
            """
            {"data":{"quote_data":{"bid_price":"10.00","ask_price":"10.02","last_trade_price":"10.01"}}}
            """), Intent());
        BrokerOrderReview malformed = RobinhoodBrokerParser.ParseReview(Json(
            """
            {"data":{"order_checks":[],"quote_data":{"bid_price":"10.00","ask_price":"10.02","last_trade_price":"10.01"}}}
            """), Intent());

        Assert.True(accepted.Accepted);
        Assert.False(missing.Accepted);
        Assert.False(malformed.Accepted);
        Assert.Contains(missing.Blockers, blocker => blocker.Contains("valid order_checks"));
    }

    [Theory]
    [InlineData("confirmed", BrokerOrderState.Confirmed, false)]
    [InlineData("locating", BrokerOrderState.Locating, false)]
    [InlineData("locate_failed", BrokerOrderState.Failed, true)]
    public void ParseOrder_MapsAdditionalRobinhoodStates(
        string state,
        BrokerOrderState expected,
        bool terminal)
    {
        IReadOnlyList<BrokerOrderSnapshot> orders = RobinhoodBrokerParser.ParseOrders(Json(
            $$$"""
            {"data":{"orders":[{"id":"order-1","symbol":"SOFI","side":"buy","state":"{{{state}}}","quantity":"2","cumulative_quantity":"0"}]}}
            """));

        BrokerOrderSnapshot order = Assert.Single(orders);
        Assert.Equal(expected, order.State);
        Assert.Equal(terminal, order.IsTerminal);
    }

    [Fact]
    public void ParseOrder_PreservesExecutionsAndTerminalState()
    {
        IReadOnlyList<BrokerOrderSnapshot> orders = RobinhoodBrokerParser.ParseOrders(Json(
            """
            {"data":{"orders":[{"id":"order-1","ref_id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","symbol":"SOFI","side":"buy","state":"filled","quantity":"2","cumulative_quantity":"2","average_price":"10.03","last_transaction_at":"2026-08-03T16:01:00Z","executions":[{"id":"execution-1","timestamp":"2026-08-03T16:01:00Z","quantity":"2","price":"10.03"}]}]}}
            """));

        BrokerOrderSnapshot order = Assert.Single(orders);
        Assert.Equal(BrokerOrderState.Filled, order.State);
        Assert.True(order.IsTerminal);
        Assert.False(order.IsOpen);
        Assert.Equal(2m, order.FilledQuantity);
        Assert.Equal(10.03m, Assert.Single(order.Executions).Price);
    }

    [Fact]
    public void ParseCancellationAccepted_RequiresExplicitAcceptance()
    {
        Assert.True(RobinhoodBrokerParser.ParseCancellationAccepted(
            Json("{\"data\":{\"accepted\":true}}")));
        Assert.False(RobinhoodBrokerParser.ParseCancellationAccepted(
            Json("{\"data\":{\"accepted\":false}}")));
    }

    private static BrokerOrderIntent Intent() =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "SOFI", BrokerOrderSide.Buy, 2m, "TEST");

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
