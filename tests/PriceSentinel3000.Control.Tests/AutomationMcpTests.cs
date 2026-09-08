using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
        Assert.Equal(new[] { "candles", "capture_chart", "configure", "events", "indicators", "library_candles", "library_datasets", "list_strategies", "pause", "results", "resume", "run_to_end", "start", "status", "step", "stop" },
            tools.Select(tool => tool.Name).OrderBy(name => name));
        foreach (string name in new[] { "candles", "indicators", "events", "capture_chart", "library_datasets", "library_candles" })
        {
            Assert.True(tools.Single(tool => tool.Name == name).ProtocolTool.Annotations!.ReadOnlyHint);
        }
        Assert.Equal(new[] { "strategy", "source" }, tools.Single(tool => tool.Name == "candles")
            .JsonSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));

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

        await client.CallToolAsync("candles", new Dictionary<string, object?>
        {
            ["kind"] = "source",
            ["afterSequence"] = 4_000_000_000L,
            ["limit"] = 25,
            ["sessionId"] = "simulation-one",
        }, cancellationToken: timeout.Token);
        var candles = requests.Last();
        Assert.Equal("candles", candles.Command);
        Assert.Equal("source", candles.Arguments.GetProperty("kind").GetString());
        Assert.Equal(4_000_000_000L, candles.Arguments.GetProperty("afterSequence").GetInt64());
        Assert.Equal(25, candles.Arguments.GetProperty("limit").GetInt32());
        Assert.Equal("simulation-one", candles.Arguments.GetProperty("sessionId").GetString());

        await client.CallToolAsync("candles", cancellationToken: timeout.Token);
        Assert.Equal("strategy", requests.Last().Arguments.GetProperty("kind").GetString());
        Assert.Equal(0, requests.Last().Arguments.GetProperty("afterSequence").GetInt64());
        Assert.Equal(50, requests.Last().Arguments.GetProperty("limit").GetInt32());
        Assert.False(requests.Last().Arguments.TryGetProperty("sessionId", out _));

        await client.CallToolAsync("library_datasets", new Dictionary<string, object?>
        {
            ["offset"] = 10, ["limit"] = 20, ["catalogHash"] = new string('a', 64),
        }, cancellationToken: timeout.Token);
        Assert.Equal("library_datasets", requests.Last().Command);
        Assert.Equal(10, requests.Last().Arguments.GetProperty("offset").GetInt32());
        Assert.Equal(new string('a', 64), requests.Last().Arguments.GetProperty("catalogHash").GetString());
        Assert.False(tools.Single(tool => tool.Name == "library_datasets").JsonSchema.GetProperty("properties").TryGetProperty("path", out _));
        await client.CallToolAsync("library_candles", new Dictionary<string, object?>
        {
            ["datasetHash"] = new string('b', 64), ["offset"] = 100, ["limit"] = 25,
        }, cancellationToken: timeout.Token);
        Assert.Equal("library_candles", requests.Last().Command);
        Assert.Equal(new string('b', 64), requests.Last().Arguments.GetProperty("datasetHash").GetString());
        Assert.Equal(25, requests.Last().Arguments.GetProperty("limit").GetInt32());
        Assert.Contains("datasetHash", tools.Single(tool => tool.Name == "library_candles").JsonSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        await client.CallToolAsync("indicators", cancellationToken: timeout.Token);
        Assert.Equal("indicators", requests.Last().Command);
        Assert.Empty(requests.Last().Arguments.EnumerateObject());

        await client.CallToolAsync("events", new Dictionary<string, object?>
        {
            ["afterSequence"] = 100,
            ["limit"] = 10,
            ["sessionId"] = "simulation-one",
        }, cancellationToken: timeout.Token);
        Assert.Equal("events", requests.Last().Command);
        Assert.Equal(100, requests.Last().Arguments.GetProperty("afterSequence").GetInt64());
        Assert.Equal(10, requests.Last().Arguments.GetProperty("limit").GetInt32());
        Assert.Equal("simulation-one", requests.Last().Arguments.GetProperty("sessionId").GetString());

        int countBeforeInvalidKind = requests.Count;
        var invalidKind = await client.CallToolAsync("candles", new Dictionary<string, object?>
        {
            ["kind"] = "future",
        }, cancellationToken: timeout.Token);
        Assert.True(invalidKind.IsError);
        Assert.Equal(countBeforeInvalidKind, requests.Count);

        var stop = await client.CallToolAsync("stop", cancellationToken: timeout.Token);
        Assert.True(stop.IsError);
        Assert.Equal("invalid_state", stop.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ChartCaptureReturnsNativeMcpImageWithMetadataAndPreservesAppErrors()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0V8AAAAASUVORK5CYII=";
        string pipeName = $"PriceSentinel3000.McpTests.{Guid.NewGuid():N}";
        var requests = new ConcurrentQueue<AutomationRequest>();
        await using var app = new AutomationPipeServer((request, _) =>
        {
            requests.Enqueue(request);
            return Task.FromResult(request.Arguments.TryGetProperty("maxWidth", out var width) && width.GetInt32() == 0
                ? AutomationResponse.Fail("invalid_arguments", "maxWidth must be positive.")
                : AutomationResponse.Ok(new
                {
                    data = png,
                    mimeType = "image/png",
                    width = 1,
                    height = 1,
                    sessionId = "chart-session",
                    candleIntervalSeconds = 15,
                    rsiLatestValue = 57.712345m,
                }));
        }, pipeName);
        app.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [typeof(Program).Assembly.Location, "--mcp", "--pipe", pipeName],
            Name = "PriceSentinel3000 image test bridge",
        }), cancellationToken: timeout.Token);

        var captured = await client.CallToolAsync("capture_chart", new Dictionary<string, object?>
        {
            ["maxWidth"] = 800,
            ["maxHeight"] = 600,
        }, cancellationToken: timeout.Token);
        Assert.False(captured.IsError);
        Assert.Equal("capture_chart", requests.Last().Command);
        Assert.Equal(800, requests.Last().Arguments.GetProperty("maxWidth").GetInt32());
        Assert.Equal(600, requests.Last().Arguments.GetProperty("maxHeight").GetInt32());
        var image = Assert.Single(captured.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Convert.FromBase64String(png), image.DecodedData.ToArray());
        var metadata = captured.StructuredContent!.Value.GetProperty("result");
        Assert.False(metadata.TryGetProperty("data", out _));
        Assert.Equal("chart-session", metadata.GetProperty("sessionId").GetString());
        Assert.Equal(57.712345m, metadata.GetProperty("rsiLatestValue").GetDecimal());
        Assert.DoesNotContain(png, Assert.Single(captured.Content.OfType<TextContentBlock>()).Text);

        await client.CallToolAsync("capture_chart", cancellationToken: timeout.Token);
        Assert.Empty(requests.Last().Arguments.EnumerateObject());

        var rejected = await client.CallToolAsync("capture_chart", new Dictionary<string, object?>
        {
            ["maxWidth"] = 0,
        }, cancellationToken: timeout.Token);
        Assert.True(rejected.IsError);
        Assert.Empty(rejected.Content.OfType<ImageContentBlock>());
        Assert.Equal("invalid_arguments", rejected.StructuredContent!.Value.GetProperty("errorCode").GetString());
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

    [Theory]
    [InlineData("candles")]
    [InlineData("indicators")]
    [InlineData("events")]
    [InlineData("capture_chart")]
    [InlineData("library_datasets")]
    [InlineData("library_candles")]
    public void CliAcceptsResearchCommands(string command) =>
        Assert.Equal(command, ControlOptions.Parse(["--command", command]).Command);
}
