using System.Net;
using System.Net.Sockets;
using PriceSentinel3000.Infrastructure.Authentication;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Core.Tests.Authentication;

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

        string? code = await RobinhoodBrowserAuthorization.AuthorizeAsync(
            authorizationUri,
            redirectUri,
            cancellation.Token,
            _ => callbackResponse = httpClient.GetStringAsync(
                new Uri(redirectUri, "?code=authorization-code&state=expected-state"),
                cancellation.Token));

        Assert.Equal("authorization-code", code);
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
            RobinhoodMcpMarketDataSource.InteractiveAuthorizationTimeout);
    }

    private static int GetAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
