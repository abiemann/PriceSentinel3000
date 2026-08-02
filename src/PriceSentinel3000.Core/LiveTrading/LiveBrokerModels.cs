using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.LiveTrading;

public enum BrokerOrderSide
{
    Buy,
    Sell,
}

public enum BrokerOrderState
{
    Unknown,
    Reviewed,
    New,
    Confirmed,
    Queued,
    Locating,
    Unconfirmed,
    PartiallyFilled,
    Filled,
    PendingCancel,
    Cancelled,
    Rejected,
    Failed,
    Voided,
    PartiallyFilledRestCancelled,
}

public sealed record BrokerAccount(
    string AccountNumber,
    bool AgenticAllowed,
    bool IsActive,
    string AccountType)
{
    public string MaskedNumber => AccountNumber.Length <= 4
        ? AccountNumber
        : $"????{AccountNumber[^4..]}";
}

public sealed record BrokerPortfolio(
    decimal TotalValue,
    decimal EquityValue,
    decimal Cash,
    decimal BuyingPower,
    string Currency);

public sealed record BrokerPosition(
    string Symbol,
    decimal Quantity,
    decimal AverageBuyPrice,
    decimal SharesAvailableForSells,
    decimal SharesHeldForSells)
{
    public bool HasPosition => Quantity != 0m;

    public static BrokerPosition Flat(string symbol) =>
        new(symbol, 0m, 0m, 0m, 0m);
}

public sealed record EquityTradability(
    string Symbol,
    bool Tradeable,
    bool FractionalTradeable,
    string State,
    string? Reason);

public sealed record BrokerOrderIntent(
    Guid ClientReferenceId,
    DateTimeOffset CreatedAtUtc,
    string Symbol,
    BrokerOrderSide Side,
    decimal Quantity,
    string Reason);

public sealed record BrokerOrderReview(
    BrokerOrderIntent Intent,
    bool Accepted,
    IReadOnlyList<string> Blockers,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? LastPrice,
    string MarketDataDisclosure,
    string RawOrderChecksJson);

public sealed record BrokerExecution(
    string Id,
    DateTimeOffset OccurredAtUtc,
    decimal Quantity,
    decimal Price);

public sealed record BrokerOrderSnapshot(
    Guid ClientReferenceId,
    string BrokerOrderId,
    string Symbol,
    BrokerOrderSide Side,
    BrokerOrderState State,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal? AveragePrice,
    string? RejectionReason,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<BrokerExecution> Executions)
{
    public bool IsOpen => !IsTerminal;

    public bool IsTerminal => State is
        BrokerOrderState.Filled or
        BrokerOrderState.Cancelled or
        BrokerOrderState.Rejected or
        BrokerOrderState.Failed or
        BrokerOrderState.Voided or
        BrokerOrderState.PartiallyFilledRestCancelled;
}

public sealed record LiveBrokerSnapshot(
    BrokerAccount Account,
    BrokerPortfolio Portfolio,
    BrokerPosition Position,
    EquityTradability Tradability,
    IReadOnlyList<BrokerOrderSnapshot> OpenOrders,
    DateTimeOffset CapturedAtUtc)
{
    public bool HasOpenOrder => OpenOrders.Any(order => order.IsOpen);
}

public interface ILiveBrokerGateway
{
    Task<BrokerAccount> GetAgenticAccountAsync(
        CancellationToken cancellationToken);

    Task<BrokerPortfolio> GetPortfolioAsync(
        string accountNumber,
        CancellationToken cancellationToken);

    Task<BrokerPosition> GetPositionAsync(
        string accountNumber,
        Instrument instrument,
        CancellationToken cancellationToken);

    Task<EquityTradability> GetTradabilityAsync(
        string accountNumber,
        string accountType,
        Instrument instrument,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrokerOrderSnapshot>> GetOpenOrdersAsync(
        string accountNumber,
        Instrument instrument,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrokerOrderSnapshot>> GetOrdersCreatedSinceAsync(
        string accountNumber,
        DateTimeOffset createdAtGteUtc,
        CancellationToken cancellationToken);

    Task<BrokerOrderReview> ReviewOrderAsync(
        string accountNumber,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot> PlaceOrderAsync(
        string accountNumber,
        BrokerOrderIntent intent,
        CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot> GetOrderAsync(
        string accountNumber,
        string brokerOrderId,
        CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot?> FindOrderByClientReferenceAsync(
        string accountNumber,
        Instrument instrument,
        Guid clientReferenceId,
        CancellationToken cancellationToken);

    Task CancelOrderAsync(
        string accountNumber,
        string brokerOrderId,
        CancellationToken cancellationToken);
}
