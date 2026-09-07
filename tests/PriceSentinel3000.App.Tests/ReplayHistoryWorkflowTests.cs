using System.Text.Json;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public Task Replay_CoarseHistoryPreservesPricesTimingAndProvenance(int interval) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog
        {
            Source = "def mean = Average(close, 2); AddOrder(OrderType.BUY_TO_OPEN, close > mean);",
        });
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromSeconds(interval));
        workspace.Broker.ReplayHistory = new[] { 10m, 11m, 9m, 12m }.Select((price, i) =>
            Bar(at.AddSeconds(i * interval), price) with
            {
                SourceIntervalSeconds = interval,
                HighPrice = price + 1m,
                LowPrice = price - 1m,
                Volume = 100m + i,
            }).ToArray();
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = interval, chartCandleIntervalSeconds = 15 },
        })).Success);
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 2 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());

        Assert.Equal(interval, vm.ChartCandleIntervalSeconds);
        Assert.All(vm.ChartCandleIntervalOptions, option => Assert.Equal(0, option.Value % interval));
        Assert.Equal($"{interval} SEC CANDLES", vm.DataResolutionLabel);
        vm.ChartCandleIntervalSeconds = 15;
        Assert.Equal(interval, vm.ChartCandleIntervalSeconds);
        JsonElement status = (await Automate(vm, "status")).Result!.Value;
        Assert.Equal(interval, status.GetProperty("replayHistory").GetProperty("SourceIntervalSeconds").GetInt32());
        JsonElement source = (await Automate(vm, "candles", new { kind = "source" })).Result!.Value.GetProperty("records");
        Assert.Equal(2, source.GetArrayLength());
        Assert.Equal(at.AddSeconds(interval), source[0].GetProperty("availableAtUtc").GetDateTimeOffset());
        Assert.Equal(at.AddSeconds(interval * 2), source[1].GetProperty("evaluationTimestampUtc").GetDateTimeOffset());
        Assert.Equal(interval, source[1].GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(12m, source[1].GetProperty("high").GetDecimal());
        Assert.Equal(101m, source[1].GetProperty("volume").GetDecimal());
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        JsonElement buy = Assert.Single(results.GetProperty("fills").EnumerateArray());
        Assert.Equal(at.AddSeconds(interval * 2), buy.GetProperty("filledAtUtc").GetDateTimeOffset());
        Assert.Equal(11m, buy.GetProperty("price").GetDecimal());
        // The first candle's low is not used as an invented intrabar stop path.
        Assert.Single(results.GetProperty("fills").EnumerateArray());

        Assert.True((await Automate(vm, "step")).Success);
        JsonElement stopped = (await Automate(vm, "events")).Result!.Value.GetProperty("records")[2];
        Assert.Equal("STOP LOSS", stopped.GetProperty("riskOverride").GetString());
        Assert.Equal(at.AddSeconds(interval * 3), stopped.GetProperty("fill").GetProperty("filledAtUtc").GetDateTimeOffset());
        Assert.True((await Automate(vm, "run_to_end")).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        JsonElement completed = (await Automate(vm, "results")).Result!.Value;
        MarketQuote[] recorded = workspace.Journal.ReadSessionQuotes(completed.GetProperty("sessionId").GetGuid(), new Instrument("SOFI")).ToArray();
        Assert.Equal(4, recorded.Length);
        Assert.All(recorded, quote => Assert.Equal(interval, quote.SourceIntervalSeconds));
        Assert.Equal(at.AddSeconds(interval * 4), recorded[^1].SourceEndsAtUtc);
        Assert.True((await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "builtin" } })).Success);
        Assert.Equal(completed.GetProperty("replayHistory").GetRawText(), (await Automate(vm, "results")).Result!.Value.GetProperty("replayHistory").GetRawText());
        // A new finer source can use the requested display interval again.
        workspace.Broker.ReplayHistory = [Bar(at, 10m)];
        Assert.True((await Automate(vm, "configure", new { mode = "Replay", settings = new { chartCandleIntervalSeconds = 15 } })).Success);
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        Assert.Equal(15, vm.ChartCandleIntervalSeconds);
    });

    [Theory]
    [InlineData(60, 30)]
    [InlineData(120, 60)]
    [InlineData(120, 300)]
    public Task Replay_IncompatibleScriptIsRejectedBeforeSessionStarts(int sourceInterval, int scriptInterval) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog());
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.ReplayHistory = [Bar(workspace.Clock.Now.AddHours(-1), 10m) with { SourceIntervalSeconds = sourceInterval }];
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = scriptInterval },
        })).Success);
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "failed");
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(scriptInterval, vm.ScriptBarIntervalSeconds);
        Assert.Equal("INTERVAL MISMATCH", vm.MarketDataStateLabel);
        Assert.Contains($"multiple of {sourceInterval} seconds", vm.StatusMessage);
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal(JsonValueKind.Null, results.GetProperty("sessionId").ValueKind);
        Assert.Empty(results.GetProperty("fills").EnumerateArray());
    });

    [Fact]
    public Task Replay_BuiltInUsesClosingTimesAndDoesNotDuplicateCurrentBar() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = PriceCandleAggregator.AlignToInterval(workspace.Clock.Now.AddHours(-1), TimeSpan.FromMinutes(1));
        workspace.Broker.ReplayHistory = [Bar(at, 10m) with { SourceIntervalSeconds = 60 }, Bar(at.AddMinutes(1), 11m) with { SourceIntervalSeconds = 60 }];
        await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "builtin" } });
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        JsonElement events = (await Automate(vm, "events")).Result!.Value.GetProperty("records");
        Assert.Equal(at.AddMinutes(1), events[0].GetProperty("evaluatedAtUtc").GetDateTimeOffset());
        Assert.Equal(at.AddMinutes(2), events[1].GetProperty("evaluatedAtUtc").GetDateTimeOffset());
        MarketQuote trigger = workspace.Broker.ReplayHistory[1] with { SourceTimestampUtc = at.AddMinutes(2) };
        var history = (IReadOnlyList<MarketQuote>)workspace.Invoke("GetExecutionHistory", trigger, true)!;
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { at.AddMinutes(1), at.AddMinutes(2) }, history.Select(quote => quote.SourceTimestampUtc));
    });
}
