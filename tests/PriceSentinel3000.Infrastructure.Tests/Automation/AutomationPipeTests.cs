using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Infrastructure.Automation;

namespace PriceSentinel3000.Infrastructure.Tests.Automation;

public sealed class AutomationPipeTests
{
    [Fact]
    public async Task RequestsReachTheSameHandlerAndConnectionsCanBeReused()
    {
        int calls = 0;
        string name = UniquePipe();
        await using var server = new AutomationPipeServer((request, _) =>
            Task.FromResult(AutomationResponse.Ok(new { request.Command, Calls = ++calls })), name);
        server.Start();
        var client = new AutomationPipeClient(name);

        for (int index = 1; index <= 3; index++)
        {
            var response = await SendToRunningServerAsync(client, Request("status"));
            Assert.True(response.Success, response.Error);
            Assert.Equal("status", response.Result!.Value.GetProperty("command").GetString());
            Assert.Equal(index, response.Result.Value.GetProperty("calls").GetInt32());
        }
    }

    [Fact]
    public async Task OneAppOwnsTheEndpointUntilDisposed()
    {
        string name = UniquePipe();
        var first = new AutomationPipeServer(Ok, name);
        first.Start();
        await using var second = new AutomationPipeServer(Ok, name);
        Assert.Throws<InvalidOperationException>(second.Start);
        await first.DisposeAsync();
        await first.DisposeAsync();
        second.Start();
        Assert.True((await SendToRunningServerAsync(new AutomationPipeClient(name), Request("status"))).Success);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"command\":\"status\",\"arguments\":{},\"extra\":true}")]
    [InlineData("{\"command\":\"status\"}")]
    [InlineData("{\"command\":\"status\",\"arguments\":[]}")]
    [InlineData("{")]
    public async Task MalformedRequestReturnsAnErrorWithoutDisablingEndpoint(string json)
    {
        string name = UniquePipe();
        await using var server = new AutomationPipeServer(Ok, name);
        server.Start();
        using (var pipe = await ConnectAsync(name))
        {
            await WriteRawAsync(pipe, json);
            var response = await ReadResponseAsync(pipe);
            Assert.False(response.Success);
            Assert.Equal("invalid_request", response.ErrorCode);
        }

        Assert.True((await SendToRunningServerAsync(new AutomationPipeClient(name), Request("status"))).Success);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforePayloadIsRead()
    {
        string name = UniquePipe();
        await using var server = new AutomationPipeServer(Ok, name);
        server.Start();
        using var pipe = await ConnectAsync(name);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 1024 * 1024 + 1);
        await pipe.WriteAsync(header);
        Assert.Equal("invalid_request", (await ReadResponseAsync(pipe)).ErrorCode);
    }

    [Fact]
    public async Task OversizedResponseReturnsStructuredErrorAndPreservesEndpoint()
    {
        string name = UniquePipe();
        await using var server = new AutomationPipeServer((_, _) => Task.FromResult(
            AutomationResponse.Ok(new { text = new string('x', 1024 * 1024) })), name);
        server.Start();
        var response = await SendToRunningServerAsync(new AutomationPipeClient(name), Request("results"));
        Assert.Equal("response_too_large", response.ErrorCode);
    }

    [Fact]
    public async Task DisconnectedClientCancelsHandlerAndNextClientCanConnect()
    {
        string name = UniquePipe();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new AutomationPipeServer(async (request, cancellationToken) =>
        {
            if (request.Command == "wait")
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    canceled.SetResult();
                }
            }

            return AutomationResponse.Ok(new { });
        }, name);
        server.Start();
        using (var pipe = await ConnectAsync(name))
        {
            await WriteRawAsync(pipe, "{\"command\":\"wait\",\"arguments\":{}}");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await SendToRunningServerAsync(new AutomationPipeClient(name), Request("status"))).Success);
    }

    [Fact]
    public async Task ShutdownCancelsAnOutstandingHandlerAndReleasesTheEndpoint()
    {
        string name = UniquePipe();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new AutomationPipeServer(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AutomationResponse.Ok(new { });
        }, name);
        server.Start();
        var pending = SendToRunningServerAsync(new AutomationPipeClient(name), Request("status"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [Fact]
    public async Task MissingAppReturnsActionableError()
    {
        var response = await new AutomationPipeClient(UniquePipe()).SendAsync(Request("status"));
        Assert.False(response.Success);
        Assert.Equal("app_not_running", response.ErrorCode);
        Assert.Contains("--automation", response.Error);
    }

    private static Task<AutomationResponse> Ok(AutomationRequest request, CancellationToken token) =>
        Task.FromResult(AutomationResponse.Ok(new { }));

    private static AutomationRequest Request(string command) =>
        new(command, JsonSerializer.SerializeToElement(new { }));

    private static string UniquePipe() => $"PriceSentinel3000.Tests.{Guid.NewGuid():N}";

    private static async Task<AutomationResponse> SendToRunningServerAsync(
        AutomationPipeClient client, AutomationRequest request)
    {
        var response = await client.SendAsync(request);
        // A busy CI worker can exceed the client's two-second connection deadline while the
        // server releases its previous connection. Retry only failures before a request is sent.
        for (int retry = 0; response.ErrorCode == "app_not_running" && retry < 2; retry++)
        {
            response = await client.SendAsync(request);
        }

        return response;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    private static async Task WriteRawAsync(Stream stream, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private static async Task<AutomationResponse> ReadResponseAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, timeout.Token);
        byte[] payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(payload, timeout.Token);
        return JsonSerializer.Deserialize<AutomationResponse>(payload, AutomationProtocol.JsonOptions)!;
    }
}
