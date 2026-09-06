using System.IO.Pipes;
using System.Text.Json;
using PriceSentinel3000.Application.Automation;

namespace PriceSentinel3000.Infrastructure.Automation;

public sealed class AutomationPipeServer : IAsyncDisposable
{
    private readonly Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeServerStream? _pipe;
    private Task? _listener;
    private Task? _disposeTask;

    public AutomationPipeServer(
        Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> handler,
        string? pipeName = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        PipeName = pipeName ?? AutomationPipeNames.Default;
    }

    public string PipeName { get; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("The automation server is already started.");
        }

        try
        {
            // Keep this single server instance alive between connections. A second app cannot
            // acquire the same endpoint, including while the first app handles a command.
            _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "The automation endpoint is already in use. Close the other automation-enabled app, or select a distinct --automation-pipe name.", exception);
        }

        _listener = ListenAsync(_pipe, _shutdown.Token);
    }

    private async Task ListenAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A disconnected or malformed client must not disable future connections.
            }
            catch (OperationCanceledException)
            {
                // A single request timed out; the endpoint remains available.
            }
            finally
            {
                if (pipe.IsConnected)
                {
                    try
                    {
                        pipe.Disconnect();
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken shutdownToken)
    {
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        requestLifetime.CancelAfter(AutomationPipeFrame.RequestTimeout);
        using var monitorLifetime = CancellationTokenSource.CreateLinkedTokenSource(requestLifetime.Token);
        Task? disconnectMonitor = null;
        AutomationResponse response;
        try
        {
            var request = await AutomationPipeFrame.ReadAsync<AutomationRequest>(pipe, requestLifetime.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.Command) || request.Arguments.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A command and an arguments object are required.");
            }

            disconnectMonitor = CancelOnDisconnectAsync(pipe, requestLifetime, monitorLifetime.Token);
            response = await _handler(request, requestLifetime.Token)
                .WaitAsync(requestLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            response = AutomationResponse.Fail("invalid_request", exception.Message);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (Exception exception)
        {
            response = AutomationResponse.Fail("command_failed", exception.Message);
        }
        finally
        {
            await monitorLifetime.CancelAsync().ConfigureAwait(false);
            if (disconnectMonitor is not null)
            {
                await disconnectMonitor.ConfigureAwait(false);
            }
        }

        try
        {
            await AutomationPipeFrame.WriteAsync(pipe, response, requestLifetime.Token).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await AutomationPipeFrame.WriteAsync(pipe,
                AutomationResponse.Fail("response_too_large", exception.Message), requestLifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected, the request expired, or the app is closing.
        }
    }

    private static async Task CancelOnDisconnectAsync(
        Stream pipe, CancellationTokenSource requestLifetime, CancellationToken cancellationToken)
    {
        try
        {
            // There is exactly one request per connection. EOF or extra input ends its lifetime.
            await pipe.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
            await requestLifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            await requestLifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
        {
            await _listener.ConfigureAwait(false);
        }

        _pipe?.Dispose();
        _shutdown.Dispose();
    }
}
