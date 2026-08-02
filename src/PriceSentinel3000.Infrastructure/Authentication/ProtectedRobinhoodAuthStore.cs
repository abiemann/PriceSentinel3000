using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace PriceSentinel3000.Infrastructure.Authentication;

public sealed class ProtectedRobinhoodAuthStore : ITokenCache
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("PriceSentinel3000.Robinhood.MCP.v1");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _tokenPath;
    private readonly string _registrationPath;

    public ProtectedRobinhoodAuthStore(string tokenPath, string registrationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationPath);
        _tokenPath = Path.GetFullPath(tokenPath);
        _registrationPath = Path.GetFullPath(registrationPath);
    }

    public ValueTask<TokenContainer?> GetTokensAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(ReadProtected<TokenContainer>(_tokenPath));
        }
    }

    public ValueTask StoreTokensAsync(
        TokenContainer tokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            WriteProtected(_tokenPath, tokens);
        }

        return ValueTask.CompletedTask;
    }

    public DynamicClientRegistrationResponse? ReadRegistration()
    {
        lock (_gate)
        {
            return ReadProtected<DynamicClientRegistrationResponse>(
                _registrationPath);
        }
    }

    public Task StoreRegistrationAsync(
        DynamicClientRegistrationResponse registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            WriteProtected(_registrationPath, registration);
        }

        return Task.CompletedTask;
    }

    private static T? ReadProtected<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            byte[] encrypted = File.ReadAllBytes(path);
            byte[] json = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or CryptographicException or JsonException)
        {
            return default;
        }
    }

    private static void WriteProtected<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[] encrypted = ProtectedData.Protect(
            json,
            Entropy,
            DataProtectionScope.CurrentUser);
        string temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
