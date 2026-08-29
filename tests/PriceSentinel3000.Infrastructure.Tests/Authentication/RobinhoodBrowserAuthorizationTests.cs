using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Authentication;
using PriceSentinel3000.Infrastructure.Authentication;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.Authentication;

public sealed class RobinhoodBrowserAuthorizationTests
{
    [Fact]
    public async Task Callback_ReturnsAuthorizationCodeAndCompletionPage()
    {
        int port = GetAvailableLoopbackPort();
        var redirectUri = new Uri($"http://127.0.0.1:{port}/callback/");
        var authorizationUri = new Uri(
            "https://example.test/authorize?state=expected-state");
        using var httpClient = new HttpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<string>? callbackResponse = null;

        AuthorizationResult? result = await RobinhoodBrowserAuthorization.AuthorizeAsync(
            new AuthorizationCallbackContext
            {
                AuthorizationUri = authorizationUri,
                RedirectUri = redirectUri,
            },
            cancellation.Token,
            _ => callbackResponse = httpClient.GetStringAsync(
                new Uri(redirectUri, "?code=authorization-code&state=expected-state&iss=https%3A%2F%2Fissuer.test"),
                cancellation.Token));

        Assert.NotNull(result);
        Assert.Equal("authorization-code", result.Code);
        Assert.Equal("expected-state", result.State);
        Assert.Equal("https://issuer.test", result.Iss);
        Assert.NotNull(callbackResponse);
        Assert.Contains(
            "authorization is complete",
            await callbackResponse,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InteractiveAuthorization_AllowsFiveMinutesForBrowserApproval()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            RobinhoodMcpGateway.InteractiveAuthorizationTimeout);
        Assert.Equal(
            "2025-11-25",
            RobinhoodMcpGateway.RobinhoodProtocolVersion);
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
