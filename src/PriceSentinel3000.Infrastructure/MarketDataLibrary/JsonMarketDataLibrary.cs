using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

/// <summary>Immutable daily documents; every index can be rebuilt from the files alone.</summary>
public sealed partial class JsonMarketDataLibrary : IMarketDataLibrary
{
    private const int SchemaVersion = 1;
    private const string GroupingZone = "America/New_York";
    private const long MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(GroupingZone);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _saveLock = new();

    public JsonMarketDataLibrary(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        EnsureNoReparsePoint(RootPath);
    }

    public string RootPath { get; }

    public MarketDataLibraryScan Scan()
    {
        var datasets = new List<HistoricalDatasetInfo>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<MarketDataLibraryDiagnostic>();
        if (!Directory.Exists(RootPath)) return new(datasets, diagnostics);
        EnsureNoReparsePoint(RootPath);
        try
        {
            foreach (string path in Directory.EnumerateFiles(RootPath, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true, MaxRecursionDepth = 6,
                         AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
                     }))
            {
                string relative = Path.GetRelativePath(RootPath, path);
                if (Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal))
                {
                    diagnostics.Add(new(relative, "interrupted_write", "An unfinished temporary file was ignored."));
                    continue;
                }
                if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    long length = new FileInfo(path).Length;
                    if (length > MaximumFileBytes) throw new InvalidDataException("The candle file exceeds the 8 MiB limit.");
                    HistoricalDataset dataset = Load(path);
                    if (hashes.Add(dataset.DatasetHash))
                        datasets.Add(Describe(dataset, relative));
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    diagnostics.Add(new(relative, "invalid_dataset", exception.Message));
                }
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
            .ThenBy(item => item.SourceIntervalSeconds).ThenBy(item => item.DatasetHash, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public IReadOnlyList<HistoricalDatasetInfo> Save(HistoricalDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        ValidateDownload(download);
        lock (_saveLock)
        {
            MarketDataLibraryScan scan = Scan();
            if (scan.Diagnostics.Any(item => item.Code == "scan_failed"))
                throw new InvalidDataException("The library must scan completely before saving another dataset.");
            var saved = new List<HistoricalDatasetInfo>();
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
        }
    }

    public HistoricalDataset Read(string datasetHash)
    {
        ValidateHash(datasetHash);
        MarketDataLibraryScan scan = Scan();
        HistoricalDatasetInfo info = scan.Datasets.FirstOrDefault(item => item.DatasetHash == datasetHash)
            ?? throw new InvalidDataException("The pinned dataset is missing or failed validation; no revision was substituted.");
        HistoricalDataset dataset = Load(Resolve(info.RelativePath));
        if (dataset.DatasetHash != datasetHash) throw new InvalidDataException("The pinned dataset changed during the read.");
        return dataset;
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
            candidates = scan.Datasets.Where(item => pins.Contains(item.DatasetHash, StringComparer.Ordinal)).ToArray();
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
                HistoricalDataset dataset = Load(Resolve(item.RelativePath));
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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
    private static void WriteAtomic(string path, byte[] content)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void EnsureReadme()
    {
        string path = Resolve("README.md");
        if (File.Exists(path)) return;
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
            PriceSentinel validates schema, timestamps, prices, derived coverage and the full hash when scanning/reading.
            Identical data fetched again retains the first file and its original fetchedAtUtc/hash. Changed content,
            provenance or coverage creates a .rev-<full-hash>.json revision without replacing old files.
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
