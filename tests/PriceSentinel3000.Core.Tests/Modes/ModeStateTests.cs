using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.Core.Tests.Modes;

public sealed class ModeStateTests
{
    [Fact]
    public void SafeDefault_StartsOffAndUnarmed()
    {
        ModeState state = ModeState.SafeDefault;

        Assert.Equal(TradingMode.Off, state.SelectedMode);
        Assert.Equal(TradingMode.Off, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }

    [Fact]
    public void SelectingLive_DoesNotArmOrActivateLiveTrading()
    {
        ModeState state = ModeState.SafeDefault.Select(TradingMode.Live);

        Assert.Equal(TradingMode.Live, state.SelectedMode);
        Assert.Equal(TradingMode.Off, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }

    [Fact]
    public void CancellingLiveSelection_RestoresTheEffectiveMode()
    {
        ModeState state = ModeState.SafeDefault
            .Select(TradingMode.Live)
            .CancelSelection();

        Assert.Equal(TradingMode.Off, state.SelectedMode);
        Assert.Equal(TradingMode.Off, state.EffectiveMode);
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
    public void AcknowledgedLive_BecomesEffectiveButRemainsDisarmed()
    {
        ModeState state = ModeState.SafeDefault
            .Select(TradingMode.Live)
            .ActivateLiveDisarmed();

        Assert.Equal(TradingMode.Live, state.SelectedMode);
        Assert.Equal(TradingMode.Live, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }

    [Fact]
    public void ActivatingLiveDisarmed_RequiresLiveSelection()
    {
        ModeState state = ModeState.SafeDefault;

        Assert.Throws<InvalidOperationException>(() => state.ActivateLiveDisarmed());
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

    [Fact]
    public void DisarmWithoutTarget_ReturnsToOff()
    {
        ModeState state = ModeState.SafeDefault
            .Select(TradingMode.Live)
            .ArmLive()
            .DisarmTo();

        Assert.Equal(TradingMode.Off, state.SelectedMode);
        Assert.Equal(TradingMode.Off, state.EffectiveMode);
        Assert.False(state.LiveArmed);
    }
}
