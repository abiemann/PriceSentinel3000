using System.Text.Json;
using System.Windows.Threading;
using PriceSentinel3000.App.ViewModels;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.Automation;

namespace PriceSentinel3000.App.Tests;

public sealed partial class SessionWorkflowTests
{
    [Fact]
    public Task AutomationPipe_ReplayPausesAndStepsAtJournalBoundariesThenRetainsStoppedResults() => host.RunAsync(async () =>
    {
        var catalog = new TestScriptCatalog { Source = "AddOrder(OrderType.BUY_TO_OPEN, close > 0);" };
        await using var workspace = new TestWorkspace(catalog);
        MainViewModel vm = workspace.ViewModel;
        Assert.True(ReplaySchedule.TryParseLocalRange("2026-09-02", "09:30", "09:32", out DateTimeOffset start, out _));
        workspace.Broker.ReplayHistory = Enumerable.Range(0, 5)
            .Select(index => Bar(start.AddSeconds(index * 15), 10m + index)).ToArray();
        await using var server = CreateAutomationServer(vm);
        server.Start();
        var client = new AutomationPipeClient(server.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        JsonElement configured = AutomationResult(await SendAutomationAsync(client, "configure", new
        {
            mode = "Replay",
            settings = new
            {
                symbol = " sofi ",
                startingBalance = 25_000m,
                strategyId = "test.thinkscript",
                scriptBarIntervalSeconds = 15,
                replayDate = "2026-09-02",
                replayTime = "09:30",
                replayEndTime = "09:32",
            },
        }, timeout.Token));
        Assert.Equal(TradingMode.Replay, vm.SelectedMode);
        Assert.Equal("SOFI", vm.Symbol);
        Assert.Equal(25_000m, vm.StartingBalance);
        Assert.Equal(vm.SelectedStrategyId, configured.GetProperty("settings").GetProperty("strategyId").GetString());
        Assert.Equal(0, workspace.Broker.Connections);

        AutomationResult(await SendAutomationAsync(client, "start", new { fast = true, pauseAfterObservations = 2 }, timeout.Token));
        JsonElement paused;
        do
        {
            paused = AutomationResult(await SendAutomationAsync(client, "status", new { }, timeout.Token));
            Assert.NotEqual("failed", paused.GetProperty("operationState").GetString());
            if (!paused.GetProperty("paused").GetBoolean()) await Task.Delay(10, timeout.Token);
        } while (!paused.GetProperty("paused").GetBoolean());

        Assert.Equal(2, paused.GetProperty("processedObservations").GetInt32());
        Assert.Equal(5, paused.GetProperty("totalObservations").GetInt32());
        Assert.Equal("paused", paused.GetProperty("operationState").GetString());
        Assert.True(paused.GetProperty("fast").GetBoolean());
        Assert.Equal(vm.IsSessionRunning, paused.GetProperty("running").GetBoolean());
        Assert.Equal(vm.IsReplayPaused, paused.GetProperty("paused").GetBoolean());
        Assert.True(vm.IsReplayPaused);
        Assert.Equal("PAUSED", vm.StrategyStateLabel);
        Assert.Equal(11m, vm.ChartPoints[^1].Close);
        Assert.False(vm.IsSessionConfigurationEditable);
        Guid sessionId = paused.GetProperty("sessionId").GetGuid();
        Assert.Equal(2, workspace.Journal.GetSummary(sessionId).QuoteCount);
        Assert.Equal(2, workspace.Journal.GetSummary(sessionId).DecisionCount);
        JsonElement pausedResults = AutomationResult(await SendAutomationAsync(client, "results", new { }, timeout.Token));
        Assert.Equal(2, pausedResults.GetProperty("decisions").GetArrayLength());
        Assert.Equal(1, pausedResults.GetProperty("fills").GetArrayLength());
        Assert.Equal(2, pausedResults.GetProperty("summary").GetProperty("quoteCount").GetInt32());
        Assert.Equal("2", ReadScalar(workspace, "SELECT count(*) FROM quotes"));

        JsonElement stepped = AutomationResult(await SendAutomationAsync(client, "step", new { }, timeout.Token));
        Assert.Equal(3, stepped.GetProperty("processedObservations").GetInt32());
        Assert.True(stepped.GetProperty("paused").GetBoolean());
        Assert.True(vm.IsReplayPaused);
        Assert.Equal(12m, vm.ChartPoints[^1].Close);
        Assert.Equal(3, workspace.Journal.GetSummary(sessionId).QuoteCount);
        Assert.Equal(3, workspace.Journal.GetSummary(sessionId).DecisionCount);
        JsonElement beforeStop = AutomationResult(await SendAutomationAsync(client, "results", new { }, timeout.Token));
        Assert.Equal(3, beforeStop.GetProperty("decisions").GetArrayLength());
        string currentPrice = vm.CurrentPrice;
        string equity = vm.AccountEquityDisplay;
        var chart = vm.ChartPoints.ToArray();

        JsonElement stopped = AutomationResult(await SendAutomationAsync(client, "stop", new { }, timeout.Token));
        Assert.False(stopped.GetProperty("running").GetBoolean());
        Assert.Equal("stopped", stopped.GetProperty("operationState").GetString());
        Assert.Equal(3, stopped.GetProperty("processedObservations").GetInt32());
        Assert.False(vm.IsSessionRunning);
        Assert.False(vm.IsReplayPaused);
        Assert.True(vm.IsSessionConfigurationEditable);
        Assert.Equal(currentPrice, vm.CurrentPrice);
        Assert.Equal(equity, vm.AccountEquityDisplay);
        Assert.Equal(chart, vm.ChartPoints.ToArray());
        JsonElement retained = AutomationResult(await SendAutomationAsync(client, "results", new { }, timeout.Token));
        Assert.Equal(sessionId, retained.GetProperty("sessionId").GetGuid());
        Assert.Equal("STOPPED_BY_USER", retained.GetProperty("outcome").GetString());
        Assert.Equal("STOPPED_BY_USER", ReadScalar(workspace, "SELECT outcome FROM sessions"));
        Assert.Equal(3, retained.GetProperty("summary").GetProperty("quoteCount").GetInt32());
        Assert.Equal(3, retained.GetProperty("summary").GetProperty("decisionCount").GetInt32());
        Assert.Equal(1, retained.GetProperty("summary").GetProperty("fillCount").GetInt32());
        foreach (string property in new[] { "settings", "account", "decisions", "fills" })
            Assert.Equal(beforeStop.GetProperty(property).GetRawText(), retained.GetProperty(property).GetRawText());
        Assert.Equal(1, workspace.Broker.Connections);
    });

    [Fact]
    public Task AutomationPipe_InvalidConfigurationIsAtomicAndLiveCommandsAreRejected() => host.RunAsync(async () =>
    {
        await using var workspace = new TestWorkspace();
        MainViewModel vm = workspace.ViewModel;
        await using var server = CreateAutomationServer(vm);
        server.Start();
        var client = new AutomationPipeClient(server.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonElement before = AutomationResult(await SendAutomationAsync(client, "status", new { }, timeout.Token));

        AutomationResponse invalid = await SendAutomationAsync(client, "configure", new
        {
            mode = "Replay",
            settings = new { symbol = "AAPL", startingBalance = 50_000m, stopLossValue = -1m },
        }, timeout.Token);
        Assert.False(invalid.Success);
        Assert.Equal("invalid_configuration", invalid.ErrorCode);
        JsonElement after = AutomationResult(await SendAutomationAsync(client, "status", new { }, timeout.Token));
        Assert.Equal(before.GetProperty("settings").GetRawText(), after.GetProperty("settings").GetRawText());
        Assert.Equal(before.GetProperty("selectedMode").GetString(), after.GetProperty("selectedMode").GetString());
        Assert.Equal("SOFI", vm.Symbol);
        Assert.Equal(10_000m, vm.StartingBalance);
        Assert.Equal(1m, vm.StopLossValue);

        vm.RequestModeSelection(TradingMode.Live);
        foreach (string command in new[] { "configure", "start", "pause", "resume", "step", "run_to_end", "stop", "strategies" })
        {
            object arguments = command == "configure" ? new { mode = "Replay" } :
                command == "strategies" ? new { refresh = true } : new { };
            AutomationResponse forbidden = await SendAutomationAsync(client, command, arguments, timeout.Token);
            Assert.False(forbidden.Success);
            Assert.Equal("live_forbidden", forbidden.ErrorCode);
        }
        JsonElement live = AutomationResult(await SendAutomationAsync(client, "status", new { }, timeout.Token));
        Assert.Equal("Live", live.GetProperty("selectedMode").GetString());
        Assert.Equal(TradingMode.Live, vm.SelectedMode);
        Assert.False(vm.LiveArmed);
        Assert.False(vm.IsSessionRunning);
        Assert.Equal(0, workspace.Broker.Connections);
    });

    private static AutomationPipeServer CreateAutomationServer(MainViewModel vm)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        return new((request, cancellationToken) => dispatcher.InvokeAsync(
            () => vm.HandleAutomationAsync(request), DispatcherPriority.Normal, cancellationToken).Task.Unwrap(),
            $"pricesentinel-workflow-{Guid.NewGuid():N}");
    }

    private static Task<AutomationResponse> SendAutomationAsync(
        AutomationPipeClient client, string command, object arguments, CancellationToken cancellationToken) =>
        client.SendAsync(new(command, JsonSerializer.SerializeToElement(arguments, AutomationProtocol.JsonOptions)), cancellationToken);

    private static JsonElement AutomationResult(AutomationResponse response)
    {
        Assert.True(response.Success, $"{response.ErrorCode}: {response.Error}");
        Assert.True(response.Result.HasValue);
        return response.Result.Value;
    }
}
