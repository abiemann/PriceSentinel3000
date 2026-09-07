using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task ReplayTradability_UsesCurrentHolidayScheduleAndPreservesOvernightEligibility() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        var vm = workspace.ViewModel;
        vm.RequestModeSelection(TradingMode.Replay);
        vm.Symbol = "NVDA";
        workspace.Clock.Now = DateTimeOffset.Parse("2026-09-07T03:22:00Z");
        workspace.Invoke("SetSymbolTradability", new EquityTradability(
            "NVDA", true, true, "active", null,
            OvernightTradeable: true,
            ExtendedHoursTradeable: true));

        Assert.True(vm.HasTradabilityResult);
        Assert.True(vm.IsTwentyFourHourEligible);
        Assert.False(vm.IsTradableNow);
        Assert.Equal("NO", vm.TradableNowText);

        workspace.Clock.Now = DateTimeOffset.Parse("2026-09-07T23:59:59Z");
        workspace.Invoke("RefreshTradableNowState");
        Assert.Equal("NO", vm.TradableNowText);

        workspace.Clock.Now = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        workspace.Invoke("RefreshTradableNowState");
        Assert.True(vm.IsTwentyFourHourEligible);
        Assert.True(vm.IsTradableNow);
        Assert.Equal("YES", vm.TradableNowText);
    });
}
