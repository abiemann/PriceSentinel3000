using System.Text;
using ModelContextProtocol.Authentication;
using PriceSentinel3000.Infrastructure.Authentication;

namespace PriceSentinel3000.Core.Tests.Authentication;

public sealed class ProtectedRobinhoodAuthStoreTests
{
    [Fact]
    public async Task Store_RoundTripsTokensAndRegistrationWithoutPlaintext()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PriceSentinel3000-tests",
            Guid.NewGuid().ToString("N"));
        string tokenPath = Path.Combine(directory, "tokens.dat");
        string registrationPath = Path.Combine(directory, "client.dat");
        var store = new ProtectedRobinhoodAuthStore(tokenPath, registrationPath);

        try
        {
            Assert.False(store.HasCachedAuthentication);

            var tokens = new TokenContainer
            {
                AccessToken = "test-access-secret",
                RefreshToken = "test-refresh-secret",
                TokenType = "Bearer",
                ObtainedAt = DateTimeOffset.UtcNow,
                ExpiresIn = 3600,
            };
            var registration = new DynamicClientRegistrationResponse
            {
                ClientId = "test-client-id",
                ClientSecret = "test-client-secret",
            };

            await store.StoreTokensAsync(tokens, CancellationToken.None);
            Assert.False(store.HasCachedAuthentication);

            await store.StoreRegistrationAsync(registration, CancellationToken.None);
            Assert.True(store.HasCachedAuthentication);

            TokenContainer? restoredTokens =
                await store.GetTokensAsync(CancellationToken.None);
            DynamicClientRegistrationResponse? restoredRegistration =
                store.ReadRegistration();
            string encryptedTokenFile = Encoding.UTF8.GetString(
                await File.ReadAllBytesAsync(tokenPath));

            Assert.Equal(tokens.AccessToken, restoredTokens?.AccessToken);
            Assert.Equal(tokens.RefreshToken, restoredTokens?.RefreshToken);
            Assert.Equal(registration.ClientId, restoredRegistration?.ClientId);
            Assert.DoesNotContain(tokens.AccessToken, encryptedTokenFile);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
