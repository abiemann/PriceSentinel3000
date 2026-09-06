using System.Text.Json;
using PriceSentinel3000.App.ViewModels;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task Research_ExposesOnlyProcessedExactCandlesAndCachedIndicators() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog
        {
            Source = "def mean = Average(close, 2); AddOrder(OrderType.BUY_TO_OPEN, close > mean);",
        });
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 12).Select(i => Bar(at.AddSeconds(i * 15), 10m + i / 1000m)).ToArray();
        await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 30 } });
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterObservations = 3 })).Success);
        await WaitForAutomation(vm, status => status.GetProperty("paused").GetBoolean());
        JsonElement source = (await Automate(vm, "candles", new { kind = "source" })).Result!.Value;
        Assert.Equal(3, source.GetProperty("records").GetArrayLength());
        JsonElement first = source.GetProperty("records")[0];
        Assert.Equal(at, first.GetProperty("startsAtUtc").GetDateTimeOffset());
        Assert.Equal(at.AddSeconds(15), first.GetProperty("endsAtUtc").GetDateTimeOffset());
        Assert.Equal(at.AddSeconds(15), first.GetProperty("evaluationTimestampUtc").GetDateTimeOffset());
        Assert.Equal(10.002m, source.GetProperty("records")[2].GetProperty("close").GetDecimal());
        JsonElement candles = (await Automate(vm, "candles")).Result!.Value;
        JsonElement candle = Assert.Single(candles.GetProperty("records").EnumerateArray());
        Assert.Equal(at.AddSeconds(30), candle.GetProperty("endsAtUtc").GetDateTimeOffset());
        Assert.Equal(10.001m, candle.GetProperty("high").GetDecimal());
        JsonElement indicators = (await Automate(vm, "indicators")).Result!.Value;
        Assert.Equal(1, indicators.GetProperty("currentWarmup").GetProperty("remainingBars").GetInt32());
        Assert.False(indicators.GetProperty("currentWarmup").GetProperty("ready").GetBoolean());
        Assert.Equal("WARMING UP", indicators.GetProperty("latestEvaluation").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, indicators.GetProperty("latestEvaluation").GetProperty("proposal").GetProperty("indicators")[0].GetProperty("value").ValueKind);
        Assert.True((await Automate(vm, "step")).Success);
        JsonElement ready = (await Automate(vm, "indicators")).Result!.Value;
        Assert.True(ready.GetProperty("currentWarmup").GetProperty("ready").GetBoolean());
        Assert.Equal(10.002m, ready.GetProperty("latestEvaluation").GetProperty("proposal").GetProperty("indicators")[0].GetProperty("value").GetDecimal());
        JsonElement events = (await Automate(vm, "events")).Result!.Value;
        Assert.Equal(4, events.GetProperty("records").GetArrayLength());
        JsonElement buy = events.GetProperty("records")[3];
        Assert.True(buy.GetProperty("scriptEvaluated").GetBoolean());
        Assert.Equal("Buy", buy.GetProperty("strategyProposal").GetProperty("signal").GetString());
        Assert.Equal(buy.GetProperty("order").GetProperty("id").GetGuid(), buy.GetProperty("fill").GetProperty("orderId").GetGuid());
        Assert.Equal(10.003m, buy.GetProperty("fill").GetProperty("price").GetDecimal());
        Assert.Equal(ready.GetProperty("evaluationSequence").GetInt64(), buy.GetProperty("evaluationSequence").GetInt64());
        // Reads do not evaluate scripts or advance playback.
        Assert.Equal(ready.GetRawText(), (await Automate(vm, "indicators")).Result!.Value.GetRawText());
        Assert.Equal(4, (await Automate(vm, "status")).Result!.Value.GetProperty("processedObservations").GetInt32());
        await Automate(vm, "stop");
    });

    [Fact]
    public Task Research_PagesStableSessionAndRejectsStaleSessionAfterRestart() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog());
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 8).Select(i => Bar(at.AddSeconds(i * 15), 10m)).ToArray();
        await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15 } });
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, status => status.GetProperty("operationState").GetString() == "completed");
        JsonElement page = (await Automate(vm, "events", new { limit = 3 })).Result!.Value;
        string session = page.GetProperty("sessionId").GetString()!;
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.False(page.GetProperty("truncated").GetBoolean());
        long next = page.GetProperty("nextSequence").GetInt64();
        JsonElement following = (await Automate(vm, "events", new { limit = 100, afterSequence = next, sessionId = session })).Result!.Value;
        Assert.Equal(5, following.GetProperty("records").GetArrayLength());
        Assert.Equal(4, following.GetProperty("records")[0].GetProperty("sequence").GetInt64());
        Assert.False(following.GetProperty("hasMore").GetBoolean());
        // Selecting another strategy does not relabel retained data.
        Assert.True((await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "builtin" } })).Success);
        Assert.Equal("script", (await Automate(vm, "indicators", new { sessionId = session })).Result!.Value.GetProperty("kind").GetString());
        await Automate(vm, "start", new { fast = true, pauseAfterObservations = 1 });
        await WaitForAutomation(vm, status => status.GetProperty("paused").GetBoolean());
        Assert.False((await Automate(vm, "events", new { sessionId = session })).Success);
        Assert.Single((await Automate(vm, "events")).Result!.Value.GetProperty("records").EnumerateArray());
        await Automate(vm, "stop");
    });

    [Theory]
    [InlineData("candles")]
    [InlineData("events")]
    public Task Research_RejectsInvalidPaginationAndUnknownArguments(string tool) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.Prepare();
        Assert.False((await Automate(workspace.ViewModel, tool, new { limit = 0 })).Success);
        Assert.False((await Automate(workspace.ViewModel, tool, new { limit = 101 })).Success);
        Assert.False((await Automate(workspace.ViewModel, tool, new { afterSequence = -1 })).Success);
        Assert.False((await Automate(workspace.ViewModel, tool, new { unexpected = true })).Success);
        Assert.False((await Automate(workspace.ViewModel, "candles", new { kind = "future" })).Success);
    });

    [Fact]
    public Task Research_RecordsRiskPreemptionAndKeepsLastActualEvaluation() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog { Source = "def price = close; AddOrder(OrderType.BUY_TO_OPEN, price > 0);" });
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = [Bar(at, 10m), Bar(at.AddSeconds(15), 9m), Bar(at.AddSeconds(30), 11m)];
        await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15, stopLossValue = 1m } });
        await Automate(vm, "start", new { fast = true, pauseAfterObservations = 2 });
        await WaitForAutomation(vm, status => status.GetProperty("paused").GetBoolean());
        JsonElement events = (await Automate(vm, "events")).Result!.Value.GetProperty("records");
        Assert.Equal("STOP LOSS", events[1].GetProperty("riskOverride").GetString());
        Assert.False(events[1].GetProperty("strategyEvaluated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("strategyProposal").ValueKind);
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("evaluationSequence").ValueKind);
        Assert.Equal("Sell", events[1].GetProperty("fill").GetProperty("side").GetString());
        JsonElement indicators = (await Automate(vm, "indicators")).Result!.Value;
        Assert.Equal(2, indicators.GetProperty("currentWarmup").GetProperty("completedBarCount").GetInt64());
        Assert.Equal(1, indicators.GetProperty("latestEvaluation").GetProperty("completedBarCount").GetInt64());
        Assert.Equal("STOP LOSS", indicators.GetProperty("lastRiskOverride").GetString());
        await Automate(vm, "stop");
    });

    [Fact]
    public Task Research_GapResetsWarmupWithoutRewritingEarlierCandles() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog { Source = "plot mean = Average(close, 2); AddOrder(OrderType.BUY_TO_OPEN, close > mean);" });
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = [Bar(at, 10m), Bar(at.AddSeconds(15), 10m), Bar(at.AddSeconds(60), 20m)];
        await Automate(vm, "configure", new { mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15 } });
        await Automate(vm, "start", new { fast = true });
        await WaitForAutomation(vm, status => status.GetProperty("operationState").GetString() == "completed");
        JsonElement warmup = (await Automate(vm, "indicators")).Result!.Value.GetProperty("currentWarmup");
        Assert.Equal(1, warmup.GetProperty("retainedBars").GetInt32());
        Assert.Equal(3, warmup.GetProperty("completedBarCount").GetInt64());
        Assert.False(warmup.GetProperty("ready").GetBoolean());
        Assert.Equal(at.AddSeconds(60), warmup.GetProperty("historyStartsAtUtc").GetDateTimeOffset());
        JsonElement candles = (await Automate(vm, "candles")).Result!.Value.GetProperty("records");
        Assert.Equal(3, candles.GetArrayLength());
        Assert.Equal(10m, candles[0].GetProperty("close").GetDecimal());
        Assert.Equal(20m, candles[2].GetProperty("close").GetDecimal());
    });
}
