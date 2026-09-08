namespace PriceSentinel3000.Core.MarketData;

public sealed record MarketQuote(
    Instrument Instrument,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset SourceTimestampUtc,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume,
    decimal? OpenPrice = null,
    decimal? HighPrice = null,
    decimal? LowPrice = null,
    decimal? ClosePrice = null,
    int SourceIntervalSeconds = 15,
    bool HasKnownVolume = true)
{
    // Historical bars cover this duration from SourceTimestampUtc. Live sampled
    // quotes are instantaneous; their observation path does not use this value.
    public DateTimeOffset SourceEndsAtUtc => SourceTimestampUtc.AddSeconds(SourceIntervalSeconds);
    public decimal Spread => Ask - Bid;
    public decimal Midpoint => (Bid + Ask) / 2m;
    public bool HasTwoSidedMarket => Bid > 0m && Ask >= Bid;
    public decimal CandleOpen => OpenPrice ?? Last;
    public decimal CandleHigh => HighPrice ?? Last;
    public decimal CandleLow => LowPrice ?? Last;
    public decimal CandleClose => ClosePrice ?? Last;
}
