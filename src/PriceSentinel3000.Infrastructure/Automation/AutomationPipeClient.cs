using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using PriceSentinel3000.Application.Automation;

namespace PriceSentinel3000.Infrastructure.Automation;

public sealed class AutomationPipeClient(string? pipeName = null)
{
    public string PipeName { get; } = pipeName ?? AutomationPipeNames.Default;

    public async Task<AutomationResponse> SendAsync(
        AutomationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return AutomationResponse.Fail("app_not_running",
                "The PriceSentinel3000 automation endpoint is unavailable or busy. Open the visible app with --automation, or retry after its current command completes. For a custom instance, match this client's --pipe to the app's --automation-pipe value.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AutomationResponse.Fail("connection_failed", exception.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AutomationPipeFrame.RequestTimeout);
        try
        {
            await AutomationPipeFrame.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            return await AutomationPipeFrame.ReadAsync<AutomationResponse>(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AutomationResponse.Fail("request_timeout",
                "The app did not respond within 30 seconds. Inspect status before retrying a command that changes the session.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return AutomationResponse.Fail("transport_error", exception.Message);
        }
    }
}
