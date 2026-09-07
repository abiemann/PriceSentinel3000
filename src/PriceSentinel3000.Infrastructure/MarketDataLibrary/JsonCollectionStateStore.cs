using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

/// <summary>Private local settings and durable work queue; never stored inside portable candle files.</summary>
public sealed class JsonCollectionStateStore(string filePath) : ICollectionStateStore
{
    private readonly string _path = Path.GetFullPath(filePath);
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public CollectionState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new();
            using var file = File.OpenRead(_path);
            if (file.Length > 32_000_000) throw new InvalidDataException("The collection settings file exceeds the size limit.");
            CollectionState state = JsonSerializer.Deserialize<CollectionState>(file, Options)
                ?? throw new InvalidDataException("The collection settings file is empty.");
            if (state.SchemaVersion != 1 || state.Jobs is null || state.Settings is null)
                throw new InvalidDataException("Unsupported or malformed collection settings.");
            return state with { Settings = state.Settings.Validate() };
        }
    }

    public void Save(CollectionState state)
    {
        lock (_gate)
        {
            if (state.SchemaVersion != 1) throw new InvalidDataException("Unsupported collection settings version.");
            CollectionState validated = state with { Settings = state.Settings.Validate() };
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, validated, Options);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
