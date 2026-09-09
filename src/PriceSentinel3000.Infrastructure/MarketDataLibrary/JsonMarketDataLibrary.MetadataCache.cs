using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

public sealed partial class JsonMarketDataLibrary
{
    private const int MaximumCachedMetadataEntries = 4096;
    private const int MaximumCachedGapRanges = 100_000;
    private readonly object _metadataGate = new();
    private readonly Dictionary<string, CachedMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private int _cachedGapRanges;

    private sealed record CachedMetadata(FileStamp Stamp, HistoricalDatasetInfo Info);
    private readonly record struct FileStamp(long Length, DateTime LastWriteUtc, DateTime CreatedUtc, FileAttributes Attributes);

    private MarketDataLibraryScan Scan(bool includeDuplicates)
    {
        lock (_metadataGate)
        {
            var seen = new HashSet<string>(_metadata.Comparer);
            try { return ScanCore(includeDuplicates, seen); }
            finally
            {
                foreach (string path in _metadata.Keys.Where(path => !seen.Contains(path)).ToArray())
                    InvalidateMetadata(path);
            }
        }
    }

    // Scans retain only validated descriptions. Selected query/merge inputs still
    // load their actual candles and validate the complete document and content hash.
    private HistoricalDatasetInfo ScanDescription(string relative)
    {
        string path = Resolve(relative);
        EnsureNoReparsePoint(path);
        FileStamp stamp = ReadStamp(path);
        if (_metadata.TryGetValue(path, out CachedMetadata? cached) && cached.Stamp == stamp)
            return cached.Info with { RelativePath = relative };
        InvalidateMetadata(path);
        if (stamp.Length > MaximumFileBytes) throw new InvalidDataException("The candle file exceeds the 8 MiB limit.");
        HistoricalDataset dataset = Load(path);
        if (ReadStamp(path) != stamp)
            throw new InvalidDataException("The candle file changed during validation. Scan the library again.");
        HistoricalDatasetInfo info = Describe(dataset, relative) with
        {
            Coverage = dataset.Coverage with { Gaps = Array.AsReadOnly(dataset.Coverage.Gaps.ToArray()) },
        };
        int gaps = info.Coverage.Gaps.Count;
        if (_metadata.Count < MaximumCachedMetadataEntries && gaps <= MaximumCachedGapRanges - _cachedGapRanges)
        {
            _metadata.Add(path, new(stamp, info));
            _cachedGapRanges += gaps;
        }
        return info;
    }

    private static FileStamp ReadStamp(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists) throw new FileNotFoundException("The candle file disappeared during the scan.", path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Library paths must not traverse symbolic links or junctions.");
        return new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, file.Attributes);
    }

    private void InvalidateMetadata(string path)
    {
        lock (_metadataGate)
        {
            if (_metadata.Remove(path, out CachedMetadata? cached)) _cachedGapRanges -= cached.Info.Coverage.Gaps.Count;
        }
    }
}
