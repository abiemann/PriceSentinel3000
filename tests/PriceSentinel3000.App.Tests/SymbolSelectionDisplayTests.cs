using System.Globalization;
using System.Text.Json;
using PriceSentinel3000.Application.Strategies;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SymbolSelection_AfterReplayClearsDisplayAndPreservesRetainedResults(bool autocompleteAfterStop) => host.RunAsync(async () =>
    {
        const string script = "AddOrder(OrderType.BUY_TO_OPEN, close > 0);";
        await using var workspace = new TestWorkspace(new TestScriptCatalog { Source = script });
        var vm = workspace.ViewModel;
        DateTimeOffset start = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 8).Select(index =>
            Bar(start.AddSeconds(index * 15), 100m + index) with { Instrument = new Instrument("NVDA") }).ToArray();
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { symbol = "NVDA", strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15 },
        })).Success);
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 2 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());
        Assert.True((await Automate(vm, autocompleteAfterStop ? "stop" : "run_to_end")).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() ==
            (autocompleteAfterStop ? "stopped" : "completed"));

        Assert.NotEmpty(vm.ChartPoints);
        Assert.True(vm.HasMarketData);
        Assert.NotEqual("FLAT", vm.PositionDisplay);
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        JsonElement sources = (await Automate(vm, "candles", new { kind = "source" })).Result!.Value;
        JsonElement indicators = (await Automate(vm, "indicators")).Result!.Value;
        Guid sessionId = results.GetProperty("sessionId").GetGuid();
        var journalSummary = workspace.Journal.GetSummary(sessionId);
        PinnedStrategy pinned = workspace.Get<PinnedStrategy>("_pinnedStrategy");
        var originalPoints = vm.ChartPoints.ToArray();
        string originalPrice = vm.CurrentPrice;
        vm.IsChartManualScale = true;
        int scaleVersion = vm.ChartScaleResetVersion;

        vm.Symbol = " nvda ";
        Assert.Equal("NVDA", vm.SymbolDisplay);
        Assert.Equal(originalPoints, vm.ChartPoints.ToArray());
        Assert.Equal(originalPrice, vm.CurrentPrice);
        Assert.True(vm.IsChartManualScale);
        Assert.Equal(scaleVersion, vm.ChartScaleResetVersion);

        if (autocompleteAfterStop)
            vm.AcceptSymbolSuggestion(new InstrumentSearchResult("MSFT", "Microsoft Corporation"));
        else
            vm.Symbol = "MSFT";

        Assert.Equal("MSFT", vm.SymbolDisplay);
        Assert.Empty(vm.ChartPoints);
        Assert.False(vm.HasMarketData);
        Assert.Equal("--", vm.CurrentPrice);
        Assert.Equal("-- / --", vm.BidAskDisplay);
        Assert.Equal("FLAT", vm.PositionDisplay);
        Assert.Equal("0", vm.EntriesDisplay);
        Assert.Equal(vm.StartingBalance.ToString("C", CultureInfo.CurrentCulture), vm.AccountEquityDisplay);
        Assert.Equal(vm.StartingBalance.ToString("C", CultureInfo.CurrentCulture), vm.BuyingPowerDisplay);
        Assert.True(vm.IsChartManualScale);
        Assert.True(vm.ChartScaleResetVersion > scaleVersion);
        Assert.True(workspace.Get<bool>("_isMarketDataConnected"));
        foreach (int interval in new[] { 120, 30, 15 })
        {
            vm.ChartCandleIntervalSeconds = interval;
            Assert.Empty(vm.ChartPoints);
            Assert.False(vm.HasMarketData);
            Assert.Equal("MSFT", vm.SymbolDisplay);
        }

        Assert.Equal(results.GetRawText(), (await Automate(vm, "results")).Result!.Value.GetRawText());
        Assert.Equal(sources.GetRawText(), (await Automate(vm, "candles", new { kind = "source" })).Result!.Value.GetRawText());
        Assert.Equal(indicators.GetRawText(), (await Automate(vm, "indicators")).Result!.Value.GetRawText());
        Assert.Equal(journalSummary, workspace.Journal.GetSummary(sessionId));
        Assert.Same(pinned, workspace.Get<PinnedStrategy>("_pinnedStrategy"));
        Assert.Equal(script, pinned.Source);
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(1, workspace.Broker.Connections);
    });
}
