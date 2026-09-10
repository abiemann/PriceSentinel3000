using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

public sealed partial class JsonMarketDataLibrary
{
    private const string ArchiveDirectory = ".archive";

    public MarketDataLibraryScan ConsolidateDailyFiles() => ConsolidateDailyFilesCore(null);

    public MarketDataLibraryScan ConsolidateDailyFiles(IProgress<int> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return ConsolidateDailyFilesCore(progress);
    }

    private MarketDataLibraryScan ConsolidateDailyFilesCore(IProgress<int>? progress)
    {
        int lastPercent = -1;
        void Report(int percent)
        {
            if (percent <= lastPercent) return;
            lastPercent = percent;
            progress?.Report(percent);
        }

        Report(0);
        MarketDataLibraryScan result = WithLibraryWriteLock(() =>
        {
            // Count file validation, daily merges, and final validation separately;
            // the final 1% is reserved until metadata cleanup and lock release finish.
            MarketDataLibraryScan before = Scan(includeDuplicates: true,
                progress is null ? null : (done, total) => Report((int)(25L * done / total)));
            Report(25);
            if (before.Diagnostics.Any(item => item.Code == "scan_failed")) return before;
            var notices = new List<MarketDataLibraryDiagnostic>();
            var groups = before.Datasets.Where(item => item.SourceIntervalSeconds == 15)
                .GroupBy(item => (item.Symbol, item.TradingDate)).ToArray();
            int completedGroups = 0;
            foreach (var group in groups)
            {
                HistoricalDatasetInfo[] files = group.ToArray();
                try
                {
                    HistoricalDataset[] sources = files.Select(LoadExact).ToArray();
                    if (sources.Length == 1 && SamePath(files[0].RelativePath, DailyPath(sources[0]))) continue;
                    PersistDaily(MergeDaily(sources), files);
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    notices.Add(new(files[0].RelativePath, "daily_merge_blocked",
                        $"Could not combine {group.Key.Symbol} on {group.Key.TradingDate:yyyy-MM-dd}: {exception.Message} Original files were retained."));
                }
                finally { Report(25 + (int)(65L * ++completedGroups / groups.Length)); }
            }
            Report(90);
            if (before.Datasets.Count > 0) EnsureReadme();
            MarketDataLibraryScan after = Scan(includeDuplicates: false,
                progress is null ? null : (done, total) => Report(90 + (int)(9L * done / total)));
            return after with { Diagnostics = after.Diagnostics.Concat(notices).ToArray() };
        });
        Report(100);
        return result;
    }

    private T WithLibraryWriteLock<T>(Func<T> action)
    {
        string identity = Path.TrimEndingDirectorySeparator(RootPath).ToUpperInvariant();
        string name = "PriceSentinel-library-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        using var mutex = new Mutex(false, name);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another process is updating this library. Try again when it finishes.");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private IEnumerable<string> ActiveFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 5,
            AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
        };
        foreach (string file in Directory.EnumerateFiles(RootPath)) yield return file;
        foreach (string directory in Directory.EnumerateDirectories(RootPath))
        {
            if (string.Equals(Path.GetFileName(directory), ArchiveDirectory, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", options)) yield return file;
        }
    }

    private bool SamePath(string left, string right) => string.Equals(Resolve(left), Resolve(right), StringComparison.OrdinalIgnoreCase);

    private static string ArchivePath(string hash) => Path.Combine(ArchiveDirectory, hash + ".json");

    private HistoricalDataset? ArchivedDataset(string hash)
    {
        string path = Resolve(ArchivePath(hash));
        if (!File.Exists(path)) return null;
        HistoricalDataset archived = Load(path);
        if (archived.DatasetHash != hash) throw new InvalidDataException("An archived dataset does not match its requested hash.");
        return archived;
    }

    private HistoricalDataset LoadExact(HistoricalDatasetInfo info)
    {
        string path = Resolve(info.RelativePath);
        try
        {
            if (File.Exists(path))
            {
                HistoricalDataset current = Load(path);
                if (current.DatasetHash == info.DatasetHash) return current;
            }
        }
        catch (FileNotFoundException) { } // A consolidation may just have archived this section.
        catch (DirectoryNotFoundException) { }
        return ArchivedDataset(info.DatasetHash)
            ?? throw new InvalidDataException("The selected dataset changed or disappeared; no replacement was selected.");
    }

    private static HistoricalDataset MergeDaily(IReadOnlyList<HistoricalDataset> sources, HistoricalDataset? incoming = null)
    {
        HistoricalDataset first = sources.FirstOrDefault(item => item.InstrumentId != UnresolvedInstrument) ?? sources[0];
        if (sources.Any(item => item.Symbol != first.Symbol || item.TradingDate != first.TradingDate ||
                item.SourceIntervalSeconds != 15 || item.Provider != first.Provider ||
                (item.InstrumentId != first.InstrumentId && !(item.InstrumentId == UnresolvedInstrument && item.Candles.Count == 0)) ||
                item.AdjustmentPolicy != first.AdjustmentPolicy || item.AdjustmentBasis != first.AdjustmentBasis ||
                item.SessionBounds is not ("regular" or "extended" or "24_5")))
            throw new InvalidDataException("Different providers, instruments, resolutions, adjustments or unsupported sessions cannot share one daily file.");

        var candles = new SortedDictionary<DateTimeOffset, HistoricalCandle>();
        // Newer provider data wins. A new save also wins equal fetch times; legacy
        // consolidation resolves equal times by the lexicographically lowest hash.
        foreach (HistoricalCandle candle in sources.OrderBy(item => item.FetchedAtUtc)
                     .ThenBy(item => ReferenceEquals(item, incoming) ? 1 : 0)
                     .ThenByDescending(item => item.DatasetHash, StringComparer.Ordinal).SelectMany(item => item.Candles))
        {
            HistoricalCandle? updated = HistoricalCandleUpdates.Apply(candle, candles.GetValueOrDefault(candle.StartsAtUtc));
            if (updated is not null) candles[candle.StartsAtUtc] = updated;
        }
        HistoricalCandle[] merged = candles.Values.ToArray();
        DateTimeOffset from = sources.Min(item => item.Coverage.RequestedFromUtc);
        DateTimeOffset through = sources.Max(item => item.Coverage.RequestedThroughUtc);
        string session = sources.Any(item => item.SessionBounds == "24_5") ? "24_5" :
            sources.Any(item => item.SessionBounds == "extended") ? "extended" : "regular";
        HistoricalDataset result = first with
        {
            DatasetHash = "", Candles = merged, SessionBounds = session,
            FetchedAtUtc = sources.Max(item => item.FetchedAtUtc),
            Coverage = Coverage(from, through, 15, merged),
            Collection = MergeCollection(sources, first.InstrumentId, merged),
        };
        result = result with { DatasetHash = Hash(result) };
        ValidateDataset(result); // Includes overlap and midnight-boundary validation.
        return result;
    }

    private HistoricalDatasetInfo PersistDaily(HistoricalDataset merged, IReadOnlyList<HistoricalDatasetInfo> originals)
    {
        string relative = DailyPath(merged);
        string path = Resolve(relative);
        EnsureNoReparsePoint(path);
        HistoricalDataset? current = File.Exists(path) ? Load(path) : null;
        if (current is not null && !originals.Any(item => item.DatasetHash == current.DatasetHash))
            throw new InvalidDataException("The daily destination contains another dataset; its original content was retained.");
        if (current is not null && SemanticHash(current) == SemanticHash(merged)) merged = current with { Collection = merged.Collection };
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(merged, JsonOptions);
        if (content.Length > MaximumFileBytes) throw new InvalidDataException("The combined daily file exceeds the 8 MiB limit.");

        // Archive exact source documents before replacing the current day. If anything
        // fails, the originals remain recoverable by hash and a later rescan can retry.
        foreach (HistoricalDatasetInfo info in originals)
        {
            if (SamePath(info.RelativePath, relative) && info.DatasetHash == merged.DatasetHash) continue;
            string originalPath = Resolve(info.RelativePath);
            HistoricalDataset original = LoadExact(info);
            string archive = Resolve(ArchivePath(original.DatasetHash));
            EnsureNoReparsePoint(archive);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            EnsureNoReparsePoint(archive);
            if (!File.Exists(archive))
                WriteAtomic(archive, File.Exists(originalPath) ? File.ReadAllBytes(originalPath) : JsonSerializer.SerializeToUtf8Bytes(original, JsonOptions));
            if (Load(archive).DatasetHash != original.DatasetHash)
                throw new InvalidDataException("The archived file failed verification; the daily files were retained.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EnsureNoReparsePoint(path);
        if (current?.DatasetHash != merged.DatasetHash ||
            JsonSerializer.Serialize(current?.Collection, JsonOptions) != JsonSerializer.Serialize(merged.Collection, JsonOptions))
            WriteAtomic(path, content, overwrite: true);
        HistoricalDataset persisted = Load(path);
        if (persisted.DatasetHash != merged.DatasetHash) throw new InvalidDataException("The merged daily file failed verification.");
        foreach (HistoricalDatasetInfo info in originals.Where(item => !SamePath(item.RelativePath, relative)))
        {
            string oldPath = Resolve(info.RelativePath);
            EnsureNoReparsePoint(oldPath);
            if (File.Exists(oldPath) && Load(oldPath).DatasetHash == info.DatasetHash)
            {
                File.Delete(oldPath);
                InvalidateMetadata(oldPath);
            }
        }
        return Describe(persisted, relative);
    }
}