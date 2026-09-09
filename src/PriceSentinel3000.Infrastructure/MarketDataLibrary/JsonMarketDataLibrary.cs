using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

/// <summary>Consolidated daily documents with archived exact-hash history.</summary>
public sealed partial class JsonMarketDataLibrary : IMarketDataLibrary
{
    private const int SchemaVersion = 1;
    private const string GroupingZone = "America/New_York";
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(GroupingZone);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public JsonMarketDataLibrary(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        EnsureNoReparsePoint(RootPath);
    }

    public string RootPath { get; }

    public MarketDataLibraryScan Scan() => Scan(includeDuplicates: false);

    private MarketDataLibraryScan ScanCore(bool includeDuplicates, HashSet<string> seen, Action<int, int>? fileProgress)
    {
        var datasets = new List<HistoricalDatasetInfo>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        long totalFileBytes = 0;
        var diagnostics = new List<MarketDataLibraryDiagnostic>();
        if (!Directory.Exists(RootPath)) return new(datasets, diagnostics);
        EnsureNoReparsePoint(RootPath);
        try
        {
            IEnumerable<string> paths = ActiveFiles();
            int total = 0;
            if (fileProgress is not null)
            {
                var discovered = new List<string>();
                try { foreach (string path in paths) discovered.Add(path); }
                catch (Exception exception) when (IsFileError(exception))
                {
                    // Keep and validate files found before a directory enumeration failure.
                    diagnostics.Add(new("", "scan_failed", exception.Message));
                }
                paths = discovered;
                total = discovered.Count;
            }
            int processed = 0;
            foreach (string path in paths)
            {
                string relative = Path.GetRelativePath(RootPath, path);
                if (Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal))
                    diagnostics.Add(new(relative, "interrupted_write", "An unfinished temporary file was ignored."));
                else if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        seen.Add(path);
                        HistoricalDatasetInfo info = ScanDescription(relative, out long fileBytes);
                        totalFileBytes += fileBytes;
                        if (hashes.Add(info.DatasetHash) || includeDuplicates)
                            datasets.Add(info);
                    }
                    catch (Exception exception) when (IsFileError(exception))
                    {
                        InvalidateMetadata(path);
                        diagnostics.Add(new(relative, "invalid_dataset", exception.Message));
                    }
                }
                fileProgress?.Invoke(++processed, total);
            }
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            diagnostics.Add(new("", "scan_failed", exception.Message));
        }
        foreach (var group in datasets.GroupBy(RevisionKey).Where(group => group.Count() > 1))
            diagnostics.Add(new(group.First().RelativePath, "conflicting_revisions",
                $"{group.Count()} revisions exist for {group.First().Symbol} on {group.First().TradingDate:yyyy-MM-dd}; select hashes or a revision policy."));
        return new(datasets.OrderBy(item => item.TradingDate).ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ThenBy(item => item.SourceIntervalSeconds).ThenBy(item => item.DatasetHash, StringComparer.Ordinal).ToArray(), diagnostics)
        {
            TotalFileBytes = totalFileBytes,
        };
    }

    public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        ValidateDownload(download);
        return WithLibraryWriteLock(() =>
        {
            MarketDataLibraryScan scan = Scan(includeDuplicates: true);
            if (scan.Diagnostics.Any(item => item.Code == "scan_failed"))
                throw new InvalidDataException("The library must scan completely before saving another dataset.");
            var saved = new List<HistoricalDatasetInfo>();
            var plans = new List<(HistoricalDataset Dataset, HistoricalDatasetInfo[] Parts)>();
            foreach (DateOnly day in Dates(download.RequestedFromUtc, download.RequestedThroughUtc))
            {
                DateTimeOffset from = Max(download.RequestedFromUtc, DayStart(day));
                DateTimeOffset through = Min(download.RequestedThroughUtc, DayStart(day.AddDays(1)));
                HistoricalCandle[] candles = download.Candles.Where(item => EasternDate(item.StartsAtUtc) == day)
                    .OrderBy(item => item.StartsAtUtc).ToArray();
                var dataset = new HistoricalDataset(SchemaVersion, GroupingZone, "", download.Provider,
                    download.InstrumentId, download.Symbol, day, download.SourceIntervalSeconds,
                    download.AdjustmentPolicy, download.AdjustmentBasis, download.SessionBounds,
                    download.FetchedAtUtc, Coverage(from, through, download.SourceIntervalSeconds, candles), candles);
                dataset = dataset with { DatasetHash = Hash(dataset) };
                ValidateDataset(dataset);
                if (dataset.SourceIntervalSeconds == 15)
                {
                    HistoricalDatasetInfo[] parts = scan.Datasets.Where(item => item.Symbol == dataset.Symbol &&
                        item.TradingDate == day && item.SourceIntervalSeconds == 15).ToArray();
                    plans.Add((MergeDaily(parts.Select(LoadExact).Append(dataset).ToArray()), parts));
                    continue;
                }
                plans.Add((dataset, []));
            }
            // Validate every day's union before changing any day in a multi-day download.
            foreach (var (dataset, parts) in plans)
            {
                if (dataset.SourceIntervalSeconds == 15)
                {
                    saved.Add(PersistDaily(dataset, parts));
                    continue;
                }
                string semanticHash = SemanticHash(dataset);
                HistoricalDatasetInfo? duplicate = scan.Datasets.Concat(saved)
                    .Where(item => RevisionKey(item) == RevisionKey(Describe(dataset, "")))
                    .OrderBy(item => item.FetchedAtUtc).ThenBy(item => item.DatasetHash, StringComparer.Ordinal)
                    .FirstOrDefault(item => SemanticHash(Load(Resolve(item.RelativePath))) == semanticHash);
                if (duplicate is not null)
                {
                    saved.Add(duplicate);
                    continue;
                }
                byte[] content = JsonSerializer.SerializeToUtf8Bytes(dataset, JsonOptions);
                if (content.Length > MaximumFileBytes) throw new InvalidDataException("The candle file exceeds the 8 MiB limit.");
                saved.Add(Persist(dataset, content));
            }
            EnsureReadme();
            return saved;
        });
    }

    public HistoricalDataset Read(string datasetHash)
    {
        ValidateHash(datasetHash);
        MarketDataLibraryScan scan = Scan();
        HistoricalDatasetInfo? info = scan.Datasets.FirstOrDefault(item => item.DatasetHash == datasetHash);
        return info is not null ? LoadExact(info) : ArchivedDataset(datasetHash)
            ?? throw new InvalidDataException("The pinned dataset is missing or failed validation; no revision was substituted.");
    }

    public HistoricalDataQueryResult Query(HistoricalDataQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateSymbol(query.Symbol);
        ValidateRange(query.FromUtc, query.ThroughUtc);
        ValidateInterval(query.SourceIntervalSeconds);
        if (!Enum.IsDefined(query.RevisionPolicy)) throw new ArgumentOutOfRangeException(nameof(query));
        MarketDataLibraryScan scan = Scan();
        var diagnostics = scan.Diagnostics.ToList();
        HistoricalDataQueryResult Failed(string code, string message)
        {
            diagnostics.Add(new("", code, message));
            return new(false, [], [], Coverage(query.FromUtc, query.ThroughUtc, query.SourceIntervalSeconds, []), diagnostics);
        }
        if (diagnostics.Any(item => item.Code == "scan_failed"))
            return Failed("incomplete_scan", "The library could not be completely scanned; no dataset selection was made.");
        bool Matches(HistoricalDatasetInfo item) => item.Symbol == query.Symbol &&
            item.SourceIntervalSeconds == query.SourceIntervalSeconds &&
            item.Coverage.RequestedFromUtc < query.ThroughUtc && item.Coverage.RequestedThroughUtc > query.FromUtc &&
            (query.Provider is null || item.Provider == query.Provider) &&
            (query.AdjustmentPolicy is null || item.AdjustmentPolicy == query.AdjustmentPolicy) &&
            (query.AdjustmentBasis is null || item.AdjustmentBasis == query.AdjustmentBasis) &&
            query.MatchesSessionBounds(item.SessionBounds);
        HistoricalDatasetInfo[] candidates;
        if (query.PinnedHashes is { Count: > 0 } pins)
        {
            if (pins.Count > 366 || pins.Distinct(StringComparer.Ordinal).Count() != pins.Count)
                return Failed("invalid_pins", "Supply at most one unique dataset hash per requested day.");
            foreach (string hash in pins) ValidateHash(hash);
            var pinned = scan.Datasets.Where(item => pins.Contains(item.DatasetHash, StringComparer.Ordinal)).ToList();
            try
            {
                foreach (string hash in pins.Where(hash => pinned.All(item => item.DatasetHash != hash)))
                    if (ArchivedDataset(hash) is { } archived) pinned.Add(Describe(archived, ArchivePath(hash)));
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                return Failed("pinned_dataset_unavailable", exception.Message);
            }
            candidates = pinned.ToArray();
            if (candidates.Length != pins.Count || candidates.Any(item => !Matches(item)))
                return Failed("pinned_dataset_unavailable", "A pinned dataset is missing, invalid, or incompatible with the requested data. No replacement was selected.");
        }
        else candidates = scan.Datasets.Where(Matches).ToArray();
        if (candidates.Select(item => IdentityKey(item with { SessionBounds = query.SessionIdentity(item.SessionBounds) }))
            .Distinct().Count() > 1)
            return Failed("incompatible_provenance", "Choose one provider, instrument, adjustment policy/basis, and session bounds; incompatible datasets are never merged.");
        if (query.PinnedHashes is { Count: > 0 } && candidates.GroupBy(item => item.TradingDate).Any(group => group.Count() > 1))
            return Failed("revision_selection_required", "Multiple daily revisions are pinned. Pin one hash per day; no replacement was selected.");
        var selected = new List<HistoricalDatasetInfo>();
        foreach (var group in candidates.GroupBy(item => (item.TradingDate,
                     SessionBounds: query.IncludeCompatibleSessions ? item.SessionBounds : "")))
        {
            if (group.Count() > 1 && (query.PinnedHashes is { Count: > 0 } || query.RevisionPolicy == HistoricalRevisionPolicy.RejectConflicts))
                return Failed("revision_selection_required", "Multiple daily revisions match. Pin one hash per day or explicitly choose LatestFetched.");
            if (query.RevisionPolicy == HistoricalRevisionPolicy.CompatibleCoverage)
            {
                selected.AddRange(group.OrderBy(item => item.FetchedAtUtc).ThenBy(item => item.DatasetHash, StringComparer.Ordinal));
                continue;
            }
            selected.Add(group.OrderByDescending(item => item.FetchedAtUtc)
                .ThenBy(item => item.DatasetHash, StringComparer.Ordinal).First());
        }
        try
        {
            HistoricalCandle[] candles = selected.OrderBy(item => item.TradingDate).SelectMany(item =>
            {
                HistoricalDataset dataset = LoadExact(item);
                if (dataset.DatasetHash != item.DatasetHash) throw new InvalidDataException("A dataset changed during the query.");
                return dataset.Candles;
            }).Where(item => item.StartsAtUtc >= query.FromUtc && item.EndsAtUtc <= query.ThroughUtc)
                .OrderBy(item => item.StartsAtUtc).ToArray();
            if (query.RevisionPolicy == HistoricalRevisionPolicy.CompatibleCoverage || query.IncludeCompatibleSessions)
            {
                if (candles.GroupBy(item => item.StartsAtUtc).Any(group => group.Distinct().Skip(1).Any()))
                    return Failed("revision_selection_required", "Saved revisions or compatible sessions disagree on candle prices or volume. Pin one revision or select matching session bounds.");
                candles = candles.Distinct().ToArray();
            }
            return new(true, selected.OrderBy(item => item.TradingDate).ToArray(), candles,
                Coverage(query.FromUtc, query.ThroughUtc, query.SourceIntervalSeconds, candles), diagnostics);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return Failed("dataset_read_failed", exception.Message);
        }
    }

    private HistoricalDataset Load(string path)
    {
        path = Resolve(Path.GetRelativePath(RootPath, path));
        EnsureNoReparsePoint(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Invalid candle file size.");
        HistoricalDataset dataset = JsonSerializer.Deserialize<HistoricalDataset>(stream, JsonOptions)
            ?? throw new InvalidDataException("The candle document is empty.");
        ValidateDataset(dataset);
        if (!string.Equals(dataset.DatasetHash, Hash(dataset), StringComparison.Ordinal))
            throw new InvalidDataException("The dataset content hash does not match; the file may be damaged or modified.");
        return dataset;
    }

    private HistoricalDatasetInfo Persist(HistoricalDataset dataset, byte[] content)
    {
        string dailyPath = DailyPath(dataset);
        foreach (string relative in new[] { dailyPath, Path.ChangeExtension(dailyPath, null) + $".rev-{dataset.DatasetHash}.json" })
        {
            string path = Resolve(relative);
            EnsureNoReparsePoint(path);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                EnsureNoReparsePoint(path);
                try
                {
                    WriteAtomic(path, content);
                    return Describe(Load(path), relative);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another collector may have atomically saved this day after
                    // our scan. Validate its file before deduplicating or revising.
                }
            }
            try
            {
                HistoricalDataset existing = Load(path);
                if (SemanticHash(existing) == SemanticHash(dataset)) return Describe(existing, relative);
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                // Preserve damaged or conflicting copies as well as valid ones.
            }
        }
        throw new InvalidDataException("The immutable revision path is already occupied by different or invalid content.");
    }

    private static HistoricalDatasetInfo Describe(HistoricalDataset dataset, string path) => new(
        dataset.DatasetHash, path, dataset.Provider, dataset.InstrumentId, dataset.Symbol, dataset.TradingDate,
        dataset.SourceIntervalSeconds, dataset.AdjustmentPolicy, dataset.AdjustmentBasis, dataset.SessionBounds,
        dataset.FetchedAtUtc, dataset.Coverage);

    private static string IdentityKey(HistoricalDatasetInfo item) =>
        JsonSerializer.Serialize(new[] { item.Provider, item.InstrumentId, item.Symbol, item.AdjustmentPolicy, item.AdjustmentBasis, item.SessionBounds });
    private static string RevisionKey(HistoricalDatasetInfo item) =>
        FormattableString.Invariant($"{IdentityKey(item)}|{item.TradingDate:yyyy-MM-dd}|{item.SourceIntervalSeconds}");
    private static string Hash(HistoricalDataset dataset) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(dataset with { DatasetHash = "" }, JsonOptions)));
    private static string SemanticHash(HistoricalDataset dataset) => Hash(dataset with { FetchedAtUtc = DateTimeOffset.UnixEpoch });
    private static string DailyPath(HistoricalDataset dataset) => Path.Combine(dataset.TradingDate.Year.ToString("0000", CultureInfo.InvariantCulture),
        dataset.TradingDate.ToString("MM - MMMM", CultureInfo.InvariantCulture), dataset.Symbol,
        FormattableString.Invariant($"{dataset.TradingDate:yyyy-MM-dd}.{dataset.SourceIntervalSeconds}s.json"));
    private string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Library file paths must be relative.");
        string result = Path.GetFullPath(Path.Combine(RootPath, relative));
        string prefix = Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The library path escapes its root.");
        return result;
    }
    private static void EnsureNoReparsePoint(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Library paths must not traverse symbolic links or junctions.");
    }
    private void WriteAtomic(string path, byte[] content, bool overwrite = false)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            InvalidateMetadata(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void EnsureReadme()
    {
        string path = Resolve("README.md");
        if (File.Exists(path))
        {
            EnsureNoReparsePoint(path);
            string original = File.ReadAllText(path);
            const string oldRule = "provenance or coverage creates a .rev-<full-hash>.json revision without replacing old files.";
            if (original.Contains(oldRule, StringComparison.Ordinal) &&
                !original.Contains("15-second daily consolidation:", StringComparison.Ordinal))
                WriteAtomic(path, Encoding.UTF8.GetBytes(original + Environment.NewLine + Environment.NewLine +
                    "15-second daily consolidation: compatible sections now merge atomically into one date.15s.json file per stock. " +
                    "Superseded exact-hash files are kept in .archive and excluded from normal scans. Overlapping candles count once; " +
                    "conflicting prices or provenance are never overwritten. Days use Eastern start dates, including midnight, month and year boundaries." +
                    Environment.NewLine), overwrite: true);
            return;
        }
        const string readme = """
            # PriceSentinel portable market data (schema 1)

            Each JSON file is self-contained. Copy day files or ticker/month/year folders into a library and rescan.
            Paths use year / numbered English month / symbol; dates are based on candle START in America/New_York.
            All timestamps are UTC. Prices and known volumes are invariant decimal strings. Null volume is unknown,
            not zero. Source interval and original OHLC are preserved; no sampled quotes or reconstructed candles belong here.
            Schema 1 requires availableAtUtc to equal the source candle close; fetchedAtUtc is collection time.
            Coverage and gaps describe only the requested UTC range and declared session bounds, not an entire 24-hour day.
            Complete price coverage with unknown volume is not complete OHLCV data.

            datasetHash is SHA-256 over the canonical schema document with datasetHash replaced by an empty string.
            PriceSentinel fully validates new or changed files during scans and validates every candle document it reads.
            Compatible 15-second sections merge into one date.15s.json file per stock and Eastern calendar date.
            Overlaps count once; conflicting candles or provenance are never overwritten. Replacement is atomic.
            Superseded files live in .archive by exact hash for prior Replay records; normal scans exclude them.
            Other native resolutions keep their separate files. Identical saves retain the original hash.
            Competing revisions require explicit hashes or the LatestFetched policy (ties use lexicographically lowest hash).
            Pin dataset hashes for repeatable experiments. Providers, instrument identities and adjustment bases never merge implicitly.
            Interrupted .tmp-* files are ignored and diagnosed. No credentials, private watchlist IDs or journal database are needed.
            """;
        EnsureNoReparsePoint(path);
        try { WriteAtomic(path, Encoding.UTF8.GetBytes(readme)); }
        catch (IOException) when (File.Exists(path)) { }
    }
    private static bool IsFileError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 16 };
        options.Converters.Add(new ExactDecimalConverter());
        return options;
    }
    private sealed class ExactDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !decimal.TryParse(reader.GetString(),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                throw new JsonException("Prices and volumes must be invariant decimal strings.");
            return value;
        }
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("0.############################", CultureInfo.InvariantCulture));
    }
}
