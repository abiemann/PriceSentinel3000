using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.Core.Tests.Modes;

public sealed class ModeStateTests
{
    [Fact]
    public void SafeDefault_StartsInUnarmedSimulation()
    {
        ModeState state = ModeState.SafeDefault;

        Assert.Equal(TradingMode.Simulation, state.SelectedMode);
        Assert.Equal(TradingMode.Simulation, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }

    [Fact]
    public void SelectingLive_DoesNotArmOrActivateLiveTrading()
    {
        ModeState state = ModeState.SafeDefault.Select(TradingMode.Live);

        Assert.Equal(TradingMode.Live, state.SelectedMode);
        Assert.Equal(TradingMode.Simulation, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }

    [Fact]
    public void CancellingLiveSelection_RestoresTheEffectiveMode()
    {
        ModeState state = ModeState.SafeDefault
            .Select(TradingMode.Live)
            .CancelSelection();

        Assert.Equal(TradingMode.Simulation, state.SelectedMode);
        Assert.Equal(TradingMode.Simulation, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }

    [Fact]
    public void ArmingLive_RequiresLiveToBeSelected()
    {
        ModeState state = ModeState.SafeDefault;

        Assert.Throws<InvalidOperationException>(() => state.ArmLive());
    }

    [Fact]
    public void ActivatingSafeMode_CannotBypassLiveAuthorization()
    {
        ModeState state = ModeState.SafeDefault;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.ActivateSafeMode(TradingMode.Live));
    }

    [Fact]
    public void CancellingLiveFromReplay_RestoresReplay()
    {
        ModeState state = ModeState.SafeDefault
            .ActivateSafeMode(TradingMode.Replay)
            .Select(TradingMode.Live)
            .CancelSelection();

        Assert.Equal(TradingMode.Replay, state.SelectedMode);
        Assert.Equal(TradingMode.Replay, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }
}
