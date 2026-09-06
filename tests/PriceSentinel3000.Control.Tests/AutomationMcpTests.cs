using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Infrastructure.Automation;

namespace PriceSentinel3000.Control.Tests;

public sealed class AutomationMcpTests
{
    [Fact]
    public async Task OfficialStdioClientDiscoversToolsAndForwardsToTheExistingAppEndpoint()
    {
        string pipeName = $"PriceSentinel3000.McpTests.{Guid.NewGuid():N}";
        var requests = new ConcurrentQueue<AutomationRequest>();
        await using var app = new AutomationPipeServer((request, _) =>
        {
            requests.Enqueue(request);
            return Task.FromResult(request.Command == "stop"
                ? AutomationResponse.Fail("invalid_state", "No simulation is running.")
                : AutomationResponse.Ok(new { operationId = "visible-app", command = request.Command }));
        }, pipeName);
        app.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [typeof(Program).Assembly.Location, "--mcp", "--pipe", pipeName],
            Name = "PriceSentinel3000 test bridge",
        }), cancellationToken: timeout.Token);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(new[] { "configure", "list_strategies", "pause", "results", "resume", "run_to_end", "start", "status", "step", "stop" },
            tools.Select(tool => tool.Name).OrderBy(name => name));

        var status = await client.CallToolAsync("status", cancellationToken: timeout.Token);
        Assert.False(status.IsError);
        Assert.Equal("visible-app", status.StructuredContent!.Value.GetProperty("result").GetProperty("operationId").GetString());

        var start = await client.CallToolAsync("start", new Dictionary<string, object?>
        {
            ["pauseAfterObservations"] = 12,
            ["fast"] = true,
        }, cancellationToken: timeout.Token);
        Assert.False(start.IsError);
        var startRequest = requests.Last();
        Assert.Equal("start", startRequest.Command);
        Assert.Equal(12, startRequest.Arguments.GetProperty("pauseAfterObservations").GetInt32());
        Assert.True(startRequest.Arguments.GetProperty("fast").GetBoolean());
        Assert.False(startRequest.Arguments.TryGetProperty("pauseAfterStrategyBars", out _));

        await client.CallToolAsync("start", cancellationToken: timeout.Token);
        Assert.Empty(requests.Last().Arguments.EnumerateObject());

        await client.CallToolAsync("configure", new Dictionary<string, object?>
        {
            ["mode"] = "Replay",
            ["settings"] = new { symbol = "SOFI", startingBalance = 5000m },
        }, cancellationToken: timeout.Token);
        var configure = requests.Last().Arguments;
        Assert.Equal("Replay", configure.GetProperty("mode").GetString());
        Assert.Equal(2, configure.GetProperty("settings").EnumerateObject().Count());

        int countBeforeInvalidMode = requests.Count;
        var invalidMode = await client.CallToolAsync("configure", new Dictionary<string, object?>
        {
            ["mode"] = "LiveTrader",
        }, cancellationToken: timeout.Token);
        Assert.True(invalidMode.IsError);
        Assert.Equal(countBeforeInvalidMode, requests.Count);

        var stop = await client.CallToolAsync("stop", cancellationToken: timeout.Token);
        Assert.True(stop.IsError);
        Assert.Equal("invalid_state", stop.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    public static TheoryData<string[]> InvalidInvocations => new()
    {
        Array.Empty<string>(),
        new[] { "--mcp", "--command", "status" },
        new[] { "--mcp", "--arguments", "{}" },
        new[] { "--command", "authenticate" },
        new[] { "--command", "status", "--arguments", "[]" },
        new[] { "--command", "status", "--pipe" },
        new[] { "--mcp", "--mcp" },
    };

    [Theory]
    [MemberData(nameof(InvalidInvocations))]
    public void CliRejectsInvalidInvocations(string[] args) =>
        Assert.Throws<ArgumentException>(() => ControlOptions.Parse(args));
    [Fact]
    public void CliMapsStrategyToolNameAndPreservesExplicitJsonValues()
    {
        var options = ControlOptions.Parse(["--command", "list_strategies", "--arguments", "{\"refresh\":true}"]);
        Assert.Equal("strategies", options.Command);
        Assert.True(options.Arguments.GetProperty("refresh").GetBoolean());
    }
}
