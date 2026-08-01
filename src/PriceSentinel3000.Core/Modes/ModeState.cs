namespace PriceSentinel3000.Core.Modes;

/// <summary>
/// Separates the mode shown by the selector from the mode allowed to execute.
/// Selecting Live alone never arms live trading.
/// </summary>
public readonly record struct ModeState
{
    private ModeState(TradingMode selected, TradingMode effective, bool liveArmed)
    {
        SelectedMode = selected;
        EffectiveMode = effective;
        LiveArmed = liveArmed;
    }

    public TradingMode SelectedMode { get; }
    public TradingMode EffectiveMode { get; }
    public bool LiveArmed { get; }

    public static ModeState SafeDefault =>
        new(TradingMode.Simulation, TradingMode.Simulation, false);

    public ModeState Select(TradingMode mode) =>
        new(mode, EffectiveMode, LiveArmed);

    public ModeState CancelSelection() =>
        new(EffectiveMode, EffectiveMode, LiveArmed);

    public ModeState ArmLive()
    {
        if (SelectedMode is not TradingMode.Live)
        {
            throw new InvalidOperationException(
                "Live mode must be selected before it can be armed.");
        }

        return new(TradingMode.Live, TradingMode.Live, true);
    }
}
