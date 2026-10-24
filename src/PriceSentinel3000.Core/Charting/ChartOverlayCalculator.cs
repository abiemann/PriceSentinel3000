using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.Charting;

public enum VwapStatus
{
    Available,
    OutsideSession,
    IncompleteSession,
    IncompleteCandle,
    MissingVolume,
    NoVolume,
}

public sealed record ChartOverlayPoint(
    decimal? Ema50,
    decimal? Ema100,
    decimal? Ema200,
    decimal? Vwap,
    VwapStatus VwapStatus);

/// <summary>
/// Display overlays over chronological chart candles. EMA is seeded from the
/// first observed close. Regular-session VWAP requires uninterrupted, complete
/// historical bars with known volume from 09:30 New York time.
/// </summary>
public static class ChartOverlayCalculator
{
    private static readonly TimeZoneInfo EasternTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static IReadOnlyList<ChartOverlayPoint> Calculate(IReadOnlyList<PriceCandle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var result = new ChartOverlayPoint[candles.Count];
        decimal? ema50 = null, ema100 = null, ema200 = null;
        decimal weightedPrice = 0m, totalVolume = 0m;
        DateOnly? currentDate = null;
        DateTimeOffset open = default, close = default, next = default;
        VwapStatus? invalid = null;
        bool tradingDay = false;

        for (int index = 0; index < candles.Count; index++)
        {
            PriceCandle candle = candles[index];
            if (!candle.IsSynthetic)
            {
                ema50 = UpdateEma(ema50, candle.Close, 50);
                ema100 = UpdateEma(ema100, candle.Close, 100);
                ema200 = UpdateEma(ema200, candle.Close, 200);
            }

            DateOnly date = EasternTradingDay.GetDate(candle.StartsAtUtc);
            if (date != currentDate)
            {
                currentDate = date;
                tradingDay = UsEquityTradingCalendar.IsTradingDay(date);
                open = AtEasternTime(date, new(9, 30));
                close = AtEasternTime(date, new(UsEquityTradingCalendar.IsEarlyClose(date) ? 13 : 16, 0));
                next = open;
                weightedPrice = totalVolume = 0m;
                invalid = null;
            }

            decimal? vwap = null;
            VwapStatus status;
            if (!tradingDay || candle.StartsAtUtc < open || candle.StartsAtUtc >= close)
                status = VwapStatus.OutsideSession;
            else
            {
                if (invalid is null)
                {
                    if (candle.IsSynthetic || candle.StartsAtUtc != next || candle.EndsAtUtc > close)
                        invalid = VwapStatus.IncompleteSession;
                    else if (!candle.HasCompleteHistory)
                        invalid = index == candles.Count - 1 ? VwapStatus.IncompleteCandle : VwapStatus.IncompleteSession;
                    else if (!candle.HasKnownVolume || candle.Volume < 0m)
                        invalid = VwapStatus.MissingVolume;
                    else
                    {
                        weightedPrice += (candle.High + candle.Low + candle.Close) / 3m * candle.Volume;
                        totalVolume += candle.Volume;
                        next = candle.EndsAtUtc;
                    }
                }
                status = invalid ?? (totalVolume > 0m ? VwapStatus.Available : VwapStatus.NoVolume);
                if (status == VwapStatus.Available)
                    vwap = weightedPrice / totalVolume;
            }

            result[index] = new(candle.IsSynthetic ? null : ema50,
                candle.IsSynthetic ? null : ema100, candle.IsSynthetic ? null : ema200, vwap, status);
        }
        return result;
    }

    private static decimal UpdateEma(decimal? previous, decimal close, int period) =>
        previous is { } value ? value + 2m / (period + 1) * (close - value) : close;

    private static DateTimeOffset AtEasternTime(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), EasternTimeZone), TimeSpan.Zero);
}
