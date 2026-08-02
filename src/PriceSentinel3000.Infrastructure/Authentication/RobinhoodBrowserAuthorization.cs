using System.Diagnostics;
using System.Net;
using System.Text;

namespace PriceSentinel3000.Infrastructure.Authentication;

public static class RobinhoodBrowserAuthorization
{
    private static readonly SemaphoreSlim AuthorizationGate = new(1, 1);

    public static Uri RedirectUri { get; } =
        new("http://127.0.0.1:17843/callback/");

    public static async Task<string?> AuthorizeAsync(
        Uri authorizationUri,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        await AuthorizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri.AbsoluteUri);
            listener.Start();

            Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });

            CancellationTokenRegistration registration =
                cancellationToken.Register(listener.Stop);
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                registration.Dispose();
            }

            IReadOnlyDictionary<string, string> expected =
                ParseQuery(authorizationUri.Query);
            IReadOnlyDictionary<string, string> actual =
                ParseQuery(context.Request.Url?.Query);
            string? error = actual.GetValueOrDefault("error");

            if (!string.IsNullOrWhiteSpace(error))
            {
                await RespondAsync(
                    context.Response,
                    "Robinhood authorization was cancelled. You can return to PriceSentinel 3000.")
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Robinhood authorization failed: {error}.");
            }

            string? expectedState = expected.GetValueOrDefault("state");
            string? actualState = actual.GetValueOrDefault("state");

            if (!string.IsNullOrEmpty(expectedState) &&
                !string.Equals(expectedState, actualState, StringComparison.Ordinal))
            {
                await RespondAsync(
                    context.Response,
                    "Authorization validation failed. Return to PriceSentinel 3000 and try again.")
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Robinhood authorization returned an invalid state value.");
            }

            string? code = actual.GetValueOrDefault("code");
            await RespondAsync(
                context.Response,
                string.IsNullOrWhiteSpace(code)
                    ? "No authorization code was received. Return to PriceSentinel 3000 and try again."
                    : "Robinhood authorization is complete. You can close this tab and return to PriceSentinel 3000.")
                .ConfigureAwait(false);
            return code;
        }
        finally
        {
            AuthorizationGate.Release();
        }
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string? query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string pair in (query ?? string.Empty).TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private static async Task RespondAsync(
        HttpListenerResponse response,
        string message)
    {
        string html = $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>PriceSentinel 3000</title></head>
            <body style="font-family:Segoe UI,sans-serif;background:#0d1520;color:#e7eef7;padding:48px">
              <h1>PriceSentinel 3000</h1><p>{WebUtility.HtmlEncode(message)}</p>
            </body></html>
            """;
        byte[] body = Encoding.UTF8.GetBytes(html);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        response.Close();
    }
}
