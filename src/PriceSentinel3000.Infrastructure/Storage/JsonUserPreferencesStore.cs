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
                if (!File.Exists(_path))
                {
                    return null;
                }

                byte[] json = File.ReadAllBytes(_path);
                PaperTraderSettings? settings =
                    JsonSerializer.Deserialize<PaperTraderSettings>(json, JsonOptions);

                if (settings is null)
                {
                    return null;
                }

                using JsonDocument document = JsonDocument.Parse(json);
                bool hasReplayEndTime = document.RootElement.TryGetProperty(
                    "replayEndTime",
                    out _);

                if (!hasReplayEndTime &&
                    ReplaySchedule.TryCalculateEndTime(
                        settings.ReplayTime,
                        settings.ReplayDurationMinutes,
                        out string migratedEndTime))
                {
                    settings = settings with
                    {
                        ReplayEndTime = migratedEndTime,
                    };
                }

                return settings;
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
