namespace PriceSentinel3000.App.ViewModels;

public enum ChartTradeMarker
{
    None,
    Buy,
    Sell,
}

public sealed record PricePointViewModel(
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    ChartTradeMarker Marker = ChartTradeMarker.None,
    decimal? MarkerPrice = null);
