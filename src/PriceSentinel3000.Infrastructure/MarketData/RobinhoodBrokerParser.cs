using System.Globalization;
using System.Text.Json;
using PriceSentinel3000.Core.LiveTrading;

namespace PriceSentinel3000.Infrastructure.MarketData;

internal static class RobinhoodBrokerParser
{
    public static BrokerAccount ParseAgenticAccount(JsonElement root)
    {
        JsonElement data = Data(root);

        if (!data.TryGetProperty("accounts", out JsonElement accounts) ||
            accounts.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException("Robinhood returned no account list.");
        }

        foreach (JsonElement account in accounts.EnumerateArray())
        {
            bool allowed = Boolean(account, "agentic_allowed");
            bool deactivated = Boolean(account, "deactivated");
            bool permanentlyDeactivated = Boolean(account, "permanently_deactivated");
            string state = String(account, "state");

            if (!allowed || deactivated || permanentlyDeactivated ||
                !string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string accountNumber = String(account, "account_number");
            if (string.IsNullOrWhiteSpace(accountNumber))
            {
                continue;
            }

            return new(
                accountNumber,
                true,
                true,
                String(account, "brokerage_account_type", String(account, "type")));
        }

        throw new InvalidOperationException(
            "Robinhood returned no active agentic-enabled brokerage account.");
    }

    public static BrokerPortfolio ParsePortfolio(JsonElement root)
    {
        JsonElement data = Data(root);
        JsonElement buyingPower = data.TryGetProperty("buying_power", out JsonElement node) &&
                                  node.ValueKind is JsonValueKind.Object
            ? node
            : default;
        return new(
            Decimal(data, "total_value"),
            Decimal(data, "equity_value"),
            Decimal(data, "cash"),
            buyingPower.ValueKind is JsonValueKind.Object
                ? Decimal(buyingPower, "buying_power")
                : 0m,
            String(data, "currency", "USD"));
    }

    public static BrokerPosition ParsePosition(JsonElement root, string symbol)
    {
        JsonElement data = Data(root);

        if (!data.TryGetProperty("positions", out JsonElement positions) ||
            positions.ValueKind is not JsonValueKind.Array)
        {
            return BrokerPosition.Flat(symbol);
        }

        foreach (JsonElement position in positions.EnumerateArray())
        {
            if (!string.Equals(String(position, "symbol"), symbol,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new(
                symbol.ToUpperInvariant(),
                Decimal(position, "quantity"),
                Decimal(position, "average_buy_price"),
                Decimal(position, "shares_available_for_sells"),
                Decimal(position, "shares_held_for_sells"));
        }

        return BrokerPosition.Flat(symbol);
    }

    public static EquityTradability ParseTradability(
        JsonElement root,
        string symbol,
        string accountType)
    {
        JsonElement data = Data(root);

        if (!data.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Robinhood returned no equity tradability results.");
        }

        foreach (JsonElement result in results.EnumerateArray())
        {
            if (!string.Equals(String(result, "symbol"), symbol,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string state = String(result, "state");
            bool generallyTradeable = Boolean(result, "tradeable") &&
                                      (string.IsNullOrWhiteSpace(state) ||
                                       string.Equals(state, "active", StringComparison.OrdinalIgnoreCase));
            bool accountTypeTradeable = true;
            string? accountTypeStatus = null;
            if (result.TryGetProperty("account_type_tradabilities", out JsonElement accountTypes) &&
                accountTypes.ValueKind is JsonValueKind.Array &&
                accountTypes.GetArrayLength() > 0)
            {
                JsonElement? matching = accountTypes.EnumerateArray()
                    .FirstOrDefault(item => string.Equals(
                        String(item, "account_type"),
                        accountType,
                        StringComparison.OrdinalIgnoreCase));
                if (matching is null || matching.Value.ValueKind is JsonValueKind.Undefined)
                {
                    accountTypeTradeable = false;
                    accountTypeStatus = "missing";
                }
                else
                {
                    accountTypeStatus = String(
                        matching.Value,
                        "account_type_tradability");
                    accountTypeTradeable = string.Equals(
                        accountTypeStatus,
                        "tradable",
                        StringComparison.OrdinalIgnoreCase);
                }
            }

            bool tradeable = generallyTradeable && accountTypeTradeable;
            string fractional = String(result, "fractional_tradability");
            bool fractionalTradeable = Boolean(result, "fractional_tradability") ||
                string.Equals(fractional, "tradable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    fractional,
                    "fractional_tradability_tradable",
                    StringComparison.OrdinalIgnoreCase);
            bool overnightTradeable = string.Equals(
                String(result, "twenty_four_seven_tradability"),
                "twenty_four_seven_tradability_tradable",
                StringComparison.Ordinal);
            bool extendedHoursTradeable = string.Equals(
                String(result, "all_day_tradability"),
                "all_day_tradability_tradable",
                StringComparison.Ordinal);
            string? reason = FirstNonEmpty(
                String(result, "reason"),
                String(result, "halt_reason"),
                !accountTypeTradeable
                    ? $"Robinhood reports {symbol} as '{accountTypeStatus}' for account type '{accountType}'."
                    : null,
                tradeable ? null : $"Robinhood reports {symbol} in state '{state}'.");
            return new(
                symbol.ToUpperInvariant(),
                tradeable,
                fractionalTradeable,
                state,
                reason,
                overnightTradeable,
                extendedHoursTradeable);
        }

        throw new InvalidOperationException(
            $"Robinhood returned no tradability result for {symbol}.");
    }

    public static BrokerOrderReview ParseReview(
        JsonElement root,
        BrokerOrderIntent intent)
    {
        JsonElement data = Data(root);
        JsonElement checks = data.TryGetProperty("order_checks", out JsonElement checkNode)
            ? checkNode
            : default;
        List<string> blockers = [];

        if (checks.ValueKind is not JsonValueKind.Object)
        {
            blockers.Add(
                "Robinhood did not return a valid order_checks object; automated placement is blocked.");
        }
        else if (checks.EnumerateObject().Any())
        {
            blockers.Add($"Robinhood pre-trade alert: {checks.GetRawText()}");
        }

        JsonElement quote = data.TryGetProperty("quote_data", out JsonElement quoteNode) &&
                            quoteNode.ValueKind is JsonValueKind.Object
            ? quoteNode
            : default;
        return new(
            intent,
            blockers.Count == 0,
            blockers,
            quote.ValueKind is JsonValueKind.Object ? NullableDecimal(quote, "bid_price") : null,
            quote.ValueKind is JsonValueKind.Object ? NullableDecimal(quote, "ask_price") : null,
            quote.ValueKind is JsonValueKind.Object ? NullableDecimal(quote, "last_trade_price") : null,
            String(data, "market_data_disclosure"),
            checks.ValueKind is JsonValueKind.Undefined ? "null" : checks.GetRawText());
    }

    public static IReadOnlyList<BrokerOrderSnapshot> ParseOrders(JsonElement root)
    {
        JsonElement data = Data(root);

        if (!data.TryGetProperty("orders", out JsonElement orders) ||
            orders.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return [.. orders.EnumerateArray().Select(order => ParseOrderNode(order, Guid.Empty))];
    }

    public static BrokerOrderSnapshot ParsePlacedOrder(
        JsonElement root,
        Guid clientReferenceId)
    {
        JsonElement data = Data(root);
        JsonElement order = data.TryGetProperty("order", out JsonElement orderNode)
            ? orderNode
            : data;
        return ParseOrderNode(order, clientReferenceId);
    }

    public static bool ParseCancellationAccepted(JsonElement root)
    {
        JsonElement data = Data(root);
        return Boolean(data, "accepted");
    }

    private static BrokerOrderSnapshot ParseOrderNode(
        JsonElement order,
        Guid fallbackReferenceId)
    {
        string refText = String(order, "ref_id");
        Guid referenceId = Guid.TryParse(refText, out Guid parsedReference)
            ? parsedReference
            : fallbackReferenceId;
        DateTimeOffset updated = Timestamp(
            order,
            "last_transaction_at",
            Timestamp(order, "created_at", DateTimeOffset.UtcNow));
        JsonElement executionsNode = order.TryGetProperty("executions", out JsonElement executions)
            ? executions
            : default;
        var parsedExecutions = new List<BrokerExecution>();

        if (executionsNode.ValueKind is JsonValueKind.Array)
        {
            foreach (JsonElement execution in executionsNode.EnumerateArray())
            {
                parsedExecutions.Add(new(
                    String(execution, "id", Guid.NewGuid().ToString("D")),
                    Timestamp(
                        execution,
                        "timestamp",
                        Timestamp(execution, "created_at", updated)),
                    Decimal(execution, "quantity"),
                    Decimal(execution, "price")));
            }
        }

        return new(
            referenceId,
            String(order, "id"),
            String(order, "symbol").ToUpperInvariant(),
            ParseSide(String(order, "side")),
            ParseState(String(order, "state")),
            Decimal(order, "quantity"),
            Decimal(order, "cumulative_quantity"),
            NullableDecimal(order, "average_price"),
            FirstNonEmpty(String(order, "reject_reason"), String(order, "rejection_reason")),
            updated,
            parsedExecutions);
    }

    private static void CollectBlockers(
        JsonElement node,
        string path,
        ICollection<string> blockers)
    {
        if (node.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty property in node.EnumerateObject())
            {
                string childPath = string.IsNullOrWhiteSpace(path)
                    ? property.Name
                    : $"{path}.{property.Name}";
                string name = property.Name.ToLowerInvariant();
                bool blockerName = name.Contains("error") ||
                                   name.Contains("reject") ||
                                   name.Contains("block") ||
                                   name.Contains("insufficient") ||
                                   name.Contains("restriction");
                bool negativePass = (name.Contains("passed") ||
                                     name.Contains("can_place") ||
                                     name.Contains("eligible")) &&
                                    property.Value.ValueKind is JsonValueKind.False;

                if ((blockerName && HasMeaningfulValue(property.Value)) || negativePass)
                {
                    blockers.Add($"{childPath}: {Display(property.Value)}");
                }
                else
                {
                    CollectBlockers(property.Value, childPath, blockers);
                }
            }
        }
        else if (node.ValueKind is JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in node.EnumerateArray())
            {
                CollectBlockers(item, $"{path}[{index++}]", blockers);
            }
        }
    }

    private static bool HasMeaningfulValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => true,
    };

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => value.GetRawText(),
    };

    private static BrokerOrderSide ParseSide(string side) =>
        string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase)
            ? BrokerOrderSide.Sell
            : BrokerOrderSide.Buy;

    private static BrokerOrderState ParseState(string state) =>
        state.ToLowerInvariant() switch
        {
            "new" => BrokerOrderState.New,
            "confirmed" => BrokerOrderState.Confirmed,
            "queued" => BrokerOrderState.Queued,
            "locating" => BrokerOrderState.Locating,
            "unconfirmed" => BrokerOrderState.Unconfirmed,
            "partially_filled" => BrokerOrderState.PartiallyFilled,
            "filled" => BrokerOrderState.Filled,
            "pending_cancelled" or "pending_cancel" => BrokerOrderState.PendingCancel,
            "cancelled" or "canceled" => BrokerOrderState.Cancelled,
            "rejected" => BrokerOrderState.Rejected,
            "failed" => BrokerOrderState.Failed,
            "locate_failed" => BrokerOrderState.Failed,
            "voided" => BrokerOrderState.Voided,
            "partially_filled_rest_cancelled" => BrokerOrderState.PartiallyFilledRestCancelled,
            _ => BrokerOrderState.Unknown,
        };

    private static JsonElement Data(JsonElement root) =>
        root.TryGetProperty("data", out JsonElement data) ? data : root;

    private static string String(
        JsonElement element,
        string property,
        string defaultValue = "")
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }

        return value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : value.GetRawText();
    }

    private static bool Boolean(JsonElement element, string property)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind is JsonValueKind.True ||
               value.ValueKind is JsonValueKind.String &&
               bool.TryParse(value.GetString(), out bool parsed) && parsed;
    }

    private static decimal Decimal(JsonElement element, string property) =>
        NullableDecimal(element, property) ?? 0m;

    private static decimal? NullableDecimal(JsonElement element, string property)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        return decimal.TryParse(
            value.GetString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset Timestamp(
        JsonElement element,
        string property,
        DateTimeOffset defaultValue)
    {
        string value = String(element, property);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : defaultValue;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
