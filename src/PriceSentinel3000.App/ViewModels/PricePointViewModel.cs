namespace PriceSentinel3000.App.ViewModels;

public enum ChartTradeMarker
{
    None,
    Buy,
    Sell,
}

public sealed record PricePointViewModel(
    DateTimeOffset TimestampUtc,
    decimal Price,
    ChartTradeMarker Marker = ChartTradeMarker.None);
