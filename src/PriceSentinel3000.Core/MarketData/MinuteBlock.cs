namespace PriceSentinel3000.Core.MarketData;

public enum PriceDirection
{
    Empty,
    Down,
    Flat,
    Up,
}

public sealed record MinuteBlock(
    int Number,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int QuoteCount,
    decimal? Open,
    decimal? High,
    decimal? Low,
    decimal? Close,
    decimal? ChangePercent,
    PriceDirection Direction);
