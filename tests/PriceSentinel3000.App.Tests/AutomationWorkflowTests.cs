using System.Text.Json;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task Automation_ValidatesWholeConfigurationBeforeChangingModeOrSettings() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        AutomationResponse rejected = await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { symbol = "AAPL", stopLossValue = -1m },
        });
        Assert.False(rejected.Success);
        Assert.Equal("invalid_configuration", rejected.ErrorCode);
        Assert.Equal("SOFI", vm.Symbol);
        Assert.Equal(TradingMode.Off, vm.SelectedMode);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.False((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "missing.thinkscript" },
        })).Success);
        Assert.Equal(TradingMode.Off, vm.SelectedMode);
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { symbol = "aapl", replaySpeed = 100m },
        })).Success);
        Assert.Equal("AAPL", vm.Symbol);
        Assert.Equal(100m, vm.ReplaySpeed);
        Assert.Equal(TradingMode.Replay, vm.EffectiveMode);
    });

    [Theory]
    [InlineData("configure")]
    [InlineData("start")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("step")]
    [InlineData("stop")]
    [InlineData("run_to_end")]
    public Task Automation_RejectsEveryMutationWhenLiveIsSelected(string command) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        workspace.ViewModel.RequestModeSelection(TradingMode.Live);
        AutomationResponse response = await Automate(workspace.ViewModel, command);
        Assert.False(response.Success);
        Assert.Equal("live_forbidden", response.ErrorCode);
        Assert.Equal(0, workspace.Broker.Connections);
        Assert.True((await Automate(workspace.ViewModel, "status")).Success);
    });

    [Fact]
    public Task Automation_RejectsUnknownFieldsAndReplayControlsInPaper() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        Assert.False((await Automate(vm, "status", new { arbitrary = true })).Success);
        Assert.False((await Automate(vm, "configure", new { mode = "Replay", settings = new { arbitrary = true } })).Success);
        Assert.True((await Automate(vm, "configure", new { mode = "PaperTrader" })).Success);
        Assert.False((await Automate(vm, "start", new { fast = true })).Success);
        Assert.False((await Automate(vm, "start", new { pauseAfterObservations = 1 })).Success);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Automation_CannotChangeLiveRiskAcknowledgement(bool acknowledged) => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(preferences: TradingSessionSettings.Default with
        {
            LiveRiskAcknowledged = acknowledged,
        });
        MainViewModel vm = workspace.ViewModel;
        AutomationResponse rejected = await Automate(vm, "configure", new
        {
            mode = "PaperTrader", settings = new { liveRiskAcknowledged = !acknowledged },
        });

        Assert.False(rejected.Success);
        Assert.Equal("invalid_arguments", rejected.ErrorCode);
        Assert.Equal(acknowledged, vm.LiveRiskAcknowledged);
        Assert.Equal(TradingMode.Off, vm.SelectedMode);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Fact]
    public Task Automation_ConfigurationPreservesLiveRiskAcknowledgement() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(preferences: TradingSessionSettings.Default with
        {
            LiveRiskAcknowledged = true,
        });
        MainViewModel vm = workspace.ViewModel;
        AutomationResponse configured = await Automate(vm, "configure", new
        {
            mode = "PaperTrader", settings = new { symbol = "AAPL" },
        });

        Assert.True(configured.Success);
        Assert.Equal("AAPL", vm.Symbol);
        Assert.True(vm.LiveRiskAcknowledged);
        Assert.True(configured.Result!.Value.GetProperty("settings").GetProperty("liveRiskAcknowledged").GetBoolean());
        Assert.False(vm.LiveArmed);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    [Fact]
    public Task Automation_StartReturnsDuringConnectionAndExplicitStopCancelsStartup() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.HoldConnection = true;
        Assert.True((await Automate(vm, "configure", new { mode = "PaperTrader" })).Success);
        AutomationResponse start = await Automate(vm, "start");
        Assert.True(start.Success);
        Assert.NotEqual(Guid.Empty, start.Result!.Value.GetProperty("operationId").GetGuid());
        Assert.True(start.Result.Value.GetProperty("starting").GetBoolean());
        Assert.False((await Automate(vm, "start")).Success);
        Assert.False((await Automate(vm, "configure", new { mode = "Replay" })).Success);
        Assert.True((await Automate(vm, "stop")).Success);
        Assert.False(vm.IsSessionRunning);
        Assert.False((await Automate(vm, "status")).Result!.Value.GetProperty("starting").GetBoolean());
    });

    [Fact]
    public Task Automation_ReplayPausesAfterCompleteBarsStepsExactlyAndRunsToEnd() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog { Source = "AddOrder(OrderType.BUY_TO_OPEN, close > 0);" };
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset startAt = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 8).Select(index => Bar(startAt.AddSeconds(index * 15), 10m + index)).ToArray();
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 30 },
        })).Success);
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterStrategyBars = 2 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());
        JsonElement paused = (await Automate(vm, "status")).Result!.Value;
        Assert.Equal(4, paused.GetProperty("processedObservations").GetInt32());
        Assert.Equal(2, paused.GetProperty("completedStrategyBars").GetInt64());
        Assert.Equal("4", ReadScalar(workspace, "SELECT count(*) FROM quotes WHERE ingestion_kind = 'Replay'"));
        Assert.Equal("4", ReadScalar(workspace, "SELECT count(*) FROM decisions"));
        Assert.Equal("1", ReadScalar(workspace, "SELECT count(*) FROM fills"));

        AutomationResponse stepped = await Automate(vm, "step");
        Assert.True(stepped.Success);
        Assert.True(stepped.Result!.Value.GetProperty("paused").GetBoolean());
        Assert.Equal(5, stepped.Result.Value.GetProperty("processedObservations").GetInt32());
        Assert.True((await Automate(vm, "resume", new { pauseAfterStrategyBars = 1 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());
        Assert.Equal(6, (await Automate(vm, "status")).Result!.Value.GetProperty("processedObservations").GetInt32());

        Assert.True((await Automate(vm, "run_to_end")).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        Assert.False(vm.IsSessionRunning);
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal("COMPLETED", results.GetProperty("outcome").GetString());
        Assert.Equal(8, results.GetProperty("summary").GetProperty("quoteCount").GetInt32());
        Assert.Equal(8, results.GetProperty("decisions").GetArrayLength());
        Assert.Equal(1, results.GetProperty("fills").GetArrayLength());
        Assert.True(results.GetProperty("account").GetProperty("positionQuantity").GetDecimal() > 0);
    });

    [Fact]
    public Task Automation_ExplicitStopFinalizesRunningReplayInsteadOfPausing() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = [Bar(at, 10m), Bar(at.AddHours(1), 11m)];
        Assert.True((await Automate(vm, "configure", new { mode = "Replay" })).Success);
        Assert.True((await Automate(vm, "start")).Success);
        Assert.True(vm.IsSessionRunning);
        Assert.True((await Automate(vm, "stop")).Success);
        Assert.False(vm.IsSessionRunning);
        Assert.False(vm.IsReplayPaused);
        Assert.Equal("STOPPED_BY_USER", (await Automate(vm, "results")).Result!.Value.GetProperty("outcome").GetString());
    });

    [Fact]
    public Task Automation_ClosingRejectsMutationsAndShutdownCancelsPendingStart() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.HoldConnection = true;
        Assert.True((await Automate(vm, "configure", new { mode = "PaperTrader" })).Success);
        Assert.True((await Automate(vm, "start")).Success);
        vm.AutomationClosing = true;
        Assert.Equal("closing", (await Automate(vm, "start")).ErrorCode);
        Assert.Equal("closing", (await Automate(vm, "stop")).ErrorCode);
        Assert.True((await Automate(vm, "status")).Success);
        Assert.True(await vm.PrepareForShutdownAsync());
        Assert.False(vm.IsSessionRunning);
        Assert.False((await Automate(vm, "status")).Result!.Value.GetProperty("starting").GetBoolean());
    });

    [Fact]
    public Task Automation_FailedSecondStartPreservesLastCompletedResults() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.ReplayHistory = [Bar(workspace.Clock.Now.AddHours(-1), 10m)];
        Assert.True((await Automate(vm, "configure", new { mode = "Replay" })).Success);
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        JsonElement completed = (await Automate(vm, "results")).Result!.Value;
        workspace.Broker.ReplayHistory = [];
        AutomationResponse failure = await Automate(vm, "start", new { fast = true });
        Assert.False(failure.Success);
        Assert.Equal("start_failed", failure.ErrorCode);
        JsonElement retained = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal(completed.GetProperty("sessionId").GetGuid(), retained.GetProperty("sessionId").GetGuid());
        Assert.Equal("COMPLETED", retained.GetProperty("outcome").GetString());
        Assert.Equal(1, retained.GetProperty("summary").GetProperty("quoteCount").GetInt32());
        Assert.Equal("failed", (await Automate(vm, "status")).Result!.Value.GetProperty("operationState").GetString());
    });

    [Fact]
    public Task Automation_NewSessionShowsFreshAccountBeforeFirstFastObservation() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog { Source = "AddOrder(OrderType.BUY_TO_OPEN, close > 0);" };
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        workspace.Broker.ReplayHistory = [Bar(workspace.Clock.Now.AddHours(-1), 10m)];
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15 },
        })).Success);
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        Assert.True((await Automate(vm, "results")).Result!.Value.GetProperty("account").GetProperty("positionQuantity").GetDecimal() > 0m);
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        JsonElement account = (await Automate(vm, "results")).Result!.Value.GetProperty("account");
        Assert.Equal(0m, account.GetProperty("positionQuantity").GetDecimal());
        Assert.Equal(vm.StartingBalance, account.GetProperty("cash").GetDecimal());
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
    });

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public Task Automation_FastAndPacedReplayProduceIdenticalDecisionsFillsAndAccount(int speed) => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog
        {
            Source = "AddOrder(OrderType.BUY_TO_OPEN, close > close[1]); AddOrder(OrderType.SELL_TO_CLOSE, close < close[1]);",
        };
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        decimal[] prices = [10m, 11m, 12m, 11m, 10m, 11m, 12m, 11m];
        DateTimeOffset at = workspace.Clock.Now.AddHours(-1);
        workspace.Broker.ReplayHistory = prices.Select((price, index) => Bar(at.AddSeconds(index * 15), price)).ToArray();
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15, replaySpeed = speed },
        })).Success);
        Assert.True((await Automate(vm, "start", new { fast = false })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        JsonElement paced = (await Automate(vm, "results")).Result!.Value;
        Assert.Contains(paced.GetProperty("fills").EnumerateArray(), fill => fill.GetProperty("side").GetString() == "Buy");
        Assert.Contains(paced.GetProperty("fills").EnumerateArray(), fill => fill.GetProperty("side").GetString() == "Sell");
        Assert.Contains(paced.GetProperty("decisions").EnumerateArray(), decision => decision.GetProperty("signal").GetString() == "StopLoss");
        Assert.True((await Automate(vm, "start", new { fast = true })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("operationState").GetString() == "completed");
        JsonElement fast = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal(paced.GetProperty("decisions").GetRawText(), fast.GetProperty("decisions").GetRawText());
        Assert.Equal(paced.GetProperty("account").GetRawText(), fast.GetProperty("account").GetRawText());
        Assert.Equal(paced.GetProperty("summary").GetRawText(), fast.GetProperty("summary").GetRawText());
        static string[] NormalizedFills(JsonElement result) => result.GetProperty("fills").EnumerateArray()
            .Select(fill => string.Join("|", fill.EnumerateObject().Where(property => property.Name != "orderId")
                .Select(property => property.Value.GetRawText()))).ToArray();
        Assert.Equal(NormalizedFills(paced), NormalizedFills(fast));
    });

    [Fact]
    public Task Automation_BarPauseWorksBeyondRollingCapacityAndResultsRemainBounded() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace(new TestScriptCatalog());
        MainViewModel vm = workspace.ViewModel;
        DateTimeOffset at = workspace.Clock.Now.AddHours(-2);
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 270).Select(index => Bar(at.AddSeconds(index * 15), 10m)).ToArray();
        Assert.True((await Automate(vm, "configure", new
        {
            mode = "Replay", settings = new { strategyId = "test.thinkscript", scriptBarIntervalSeconds = 15 },
        })).Success);
        Assert.True((await Automate(vm, "start", new { fast = true, pauseAfterStrategyBars = 260 })).Success);
        await WaitForAutomation(vm, value => value.GetProperty("paused").GetBoolean());
        JsonElement paused = (await Automate(vm, "status")).Result!.Value;
        Assert.Equal(260, paused.GetProperty("completedStrategyBars").GetInt64());
        Assert.Equal(260, paused.GetProperty("processedObservations").GetInt32());
        JsonElement results = (await Automate(vm, "results")).Result!.Value;
        Assert.Equal(260, results.GetProperty("summary").GetProperty("decisionCount").GetInt32());
        Assert.Equal(200, results.GetProperty("decisions").GetArrayLength());
        Assert.True((await Automate(vm, "stop")).Success);
    });

    private static Task<AutomationResponse> Automate(MainViewModel vm, string command, object? arguments = null) =>
        vm.HandleAutomationAsync(new(command, JsonSerializer.SerializeToElement(arguments ?? new { }, AutomationProtocol.JsonOptions)));

    private static async Task WaitForAutomation(MainViewModel vm, Func<JsonElement, bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            AutomationResponse status = await Automate(vm, "status");
            Assert.True(status.Success, status.Error);
            if (condition(status.Result!.Value)) return;
            await Task.Delay(10);
        }
        Assert.Fail("Automation did not reach the expected state within five seconds.");
    }
}
