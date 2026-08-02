namespace PriceSentinel3000.Core.MarketData;

public sealed record MarketQuote(
    Instrument Instrument,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset SourceTimestampUtc,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume)
{
    public decimal Spread => Ask - Bid;
    public decimal Midpoint => (Bid + Ask) / 2m;
    public bool HasTwoSidedMarket => Bid > 0m && Ask >= Bid;
}
