using System.Text.Json;
using PriceSentinel3000.Core.Configuration;

namespace PriceSentinel3000.Infrastructure.Storage;

public sealed class JsonUserPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public JsonUserPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public PaperTraderSettings? Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<PaperTraderSettings>(
                        File.ReadAllBytes(_path),
                        JsonOptions)
                    : null;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                NotSupportedException)
            {
                return null;
            }
        }
    }

    public bool Save(PaperTraderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            string temporaryPath = $"{_path}.tmp";

            try
            {
                string? directory = Path.GetDirectoryName(_path);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
                File.WriteAllBytes(temporaryPath, json);
                File.Move(temporaryPath, _path, overwrite: true);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                NotSupportedException)
            {
                TryDeleteTemporaryFile(temporaryPath);
                return false;
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
