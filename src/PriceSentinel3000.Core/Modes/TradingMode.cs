namespace PriceSentinel3000.Core.Modes;

/// <summary>
/// Determines where market events come from and whether orders can leave the app.
/// </summary>
public enum TradingMode
{
    Off,
    Replay,
    PaperTrader,
    Live,
}
