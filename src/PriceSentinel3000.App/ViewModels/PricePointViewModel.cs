using PriceSentinel3000.Core.Charting;

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
    decimal? MarkerPrice = null,
    bool IsSynthetic = false,
    decimal? Ema50 = null,
    decimal? Ema100 = null,
    decimal? Ema200 = null,
    decimal? Vwap = null,
    VwapStatus VwapStatus = VwapStatus.IncompleteSession);
