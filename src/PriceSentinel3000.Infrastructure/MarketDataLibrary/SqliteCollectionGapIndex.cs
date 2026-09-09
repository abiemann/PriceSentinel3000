using Microsoft.Data.Sqlite;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.Infrastructure.MarketDataLibrary;

/// <summary>Remembers validated broker responses; saved-file gaps alone are not evidence of unavailable data.</summary>
public sealed class SqliteCollectionGapIndex : ICollectionGapIndex
{
    private readonly string _databasePath;
    private readonly string _providerIdentity;
    private bool _initialized;

    public SqliteCollectionGapIndex(string databasePath, string providerIdentity = "Robinhood")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentity);
        _databasePath = Path.GetFullPath(databasePath);
        _providerIdentity = providerIdentity;
    }

    public void Initialize()
    {
        if (_initialized && File.Exists(_databasePath)) return;
        using SqliteConnection connection = OpenConnection(initialize: true);
    }

    public CollectionGapSnapshot Query(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc, DateTimeOffset nowUtc)
    {
        ValidateKeyAndRange(key, fromUtc, throughUtc);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand scope = connection.CreateCommand();
        scope.CommandText = "SELECT scope_id, has_returned_candles FROM collection_gap_scopes WHERE " + ScopePredicate;
        AddScopeParameters(scope, key);
        long scopeId;
        bool hasReturnedCandles;
        using (SqliteDataReader reader = scope.ExecuteReader())
        {
            if (!reader.Read()) return new CollectionGapSnapshot([], false);
            scopeId = reader.GetInt64(0);
            hasReturnedCandles = reader.GetBoolean(1);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT from_ticks, through_ticks FROM collection_unavailable_ranges
            WHERE scope_id = $scope AND from_ticks < $through AND through_ticks > $from
              AND (retry_after_ticks IS NULL OR retry_after_ticks > $now)
            ORDER BY from_ticks;
            """;
        command.Parameters.AddWithValue("$scope", scopeId);
        command.Parameters.AddWithValue("$from", fromUtc.UtcTicks);
        command.Parameters.AddWithValue("$through", throughUtc.UtcTicks);
        command.Parameters.AddWithValue("$now", nowUtc.UtcTicks);
        var gaps = new List<HistoricalGap>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var gap = new HistoricalGap(Utc(Math.Max(reader.GetInt64(0), fromUtc.UtcTicks)),
                    Utc(Math.Min(reader.GetInt64(1), throughUtc.UtcTicks)));
                if (gaps.Count > 0 && gaps[^1].ThroughUtc >= gap.FromUtc)
                    gaps[^1] = gaps[^1] with { ThroughUtc = gap.ThroughUtc > gaps[^1].ThroughUtc ? gap.ThroughUtc : gaps[^1].ThroughUtc };
                else gaps.Add(gap);
            }
        }
        return new CollectionGapSnapshot(gaps, hasReturnedCandles);
    }

    public void RecordAttempt(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc,
        IReadOnlyList<HistoricalGap> unavailableRanges, bool receivedCandles, DateTimeOffset checkedAtUtc, DateTimeOffset? retryAfterUtc)
    {
        ValidateKeyAndRange(key, fromUtc, throughUtc);
        ArgumentNullException.ThrowIfNull(unavailableRanges);
        foreach (HistoricalGap gap in unavailableRanges)
        {
            if (gap is null || gap.FromUtc >= gap.ThroughUtc || gap.FromUtc < fromUtc || gap.ThroughUtc > throughUtc)
                throw new ArgumentException("Unavailable ranges must lie inside the completed attempt and have positive duration.", nameof(unavailableRanges));
        }
        if (retryAfterUtc <= checkedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(retryAfterUtc), "A retry time must follow the completed attempt.");

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand scope = connection.CreateCommand();
        scope.Transaction = transaction;
        scope.CommandText = """
            INSERT INTO collection_gap_scopes
                (provider, symbol, instrument_id, session_date, session_bounds, adjustment_policy, adjustment_basis, source_interval_seconds, has_returned_candles)
            VALUES ($provider, $symbol, $instrument, $date, $session, $policy, $basis, $interval, $received)
            ON CONFLICT (provider, symbol, instrument_id, session_date, session_bounds, adjustment_policy, adjustment_basis, source_interval_seconds)
            DO UPDATE SET has_returned_candles = MAX(has_returned_candles, excluded.has_returned_candles)
            RETURNING scope_id;
            """;
        AddScopeParameters(scope, key);
        scope.Parameters.AddWithValue("$received", receivedCandles ? 1 : 0);
        long scopeId = (long)scope.ExecuteScalar()!;

        using SqliteCommand prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM collection_unavailable_ranges WHERE scope_id = $scope AND retry_after_ticks <= $now;";
        prune.Parameters.AddWithValue("$scope", scopeId);
        prune.Parameters.AddWithValue("$now", checkedAtUtc.UtcTicks);
        prune.ExecuteNonQuery();

        // Include touching neighbors so a new observation can coalesce without scanning unrelated days or stocks.
        using SqliteCommand existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT from_ticks, through_ticks, retry_after_ticks FROM collection_unavailable_ranges
            WHERE scope_id = $scope AND from_ticks <= $through AND through_ticks >= $from
            ORDER BY from_ticks;
            """;
        existing.Parameters.AddWithValue("$scope", scopeId);
        existing.Parameters.AddWithValue("$from", fromUtc.UtcTicks);
        existing.Parameters.AddWithValue("$through", throughUtc.UtcTicks);
        var replacements = new List<Block>();
        using (SqliteDataReader reader = existing.ExecuteReader())
        {
            while (reader.Read())
            {
                long from = reader.GetInt64(0), through = reader.GetInt64(1);
                long? retry = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                if (from < fromUtc.UtcTicks) replacements.Add(new Block(from, Math.Min(through, fromUtc.UtcTicks), retry));
                if (through > throughUtc.UtcTicks) replacements.Add(new Block(Math.Max(from, throughUtc.UtcTicks), through, retry));
            }
        }
        existing.CommandText = "DELETE FROM collection_unavailable_ranges WHERE scope_id = $scope AND from_ticks <= $through AND through_ticks >= $from;";
        existing.ExecuteNonQuery();

        replacements.AddRange(unavailableRanges.Select(gap => new Block(gap.FromUtc.UtcTicks, gap.ThroughUtc.UtcTicks, retryAfterUtc?.UtcTicks)));
        var merged = new List<Block>();
        foreach (Block block in replacements.OrderBy(block => block.FromTicks).ThenBy(block => block.ThroughTicks))
        {
            if (merged.Count > 0 && merged[^1].RetryAfterTicks == block.RetryAfterTicks && merged[^1].ThroughTicks >= block.FromTicks)
                merged[^1] = merged[^1] with { ThroughTicks = Math.Max(merged[^1].ThroughTicks, block.ThroughTicks) };
            else merged.Add(block);
        }
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO collection_unavailable_ranges (scope_id, from_ticks, through_ticks, retry_after_ticks) VALUES ($scope, $from, $through, $retry);";
        insert.Parameters.AddWithValue("$scope", scopeId);
        SqliteParameter fromParameter = insert.Parameters.Add("$from", SqliteType.Integer);
        SqliteParameter throughParameter = insert.Parameters.Add("$through", SqliteType.Integer);
        SqliteParameter retryParameter = insert.Parameters.Add("$retry", SqliteType.Integer);
        foreach (Block block in merged)
        {
            fromParameter.Value = block.FromTicks;
            throughParameter.Value = block.ThroughTicks;
            retryParameter.Value = (object?)block.RetryAfterTicks ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection OpenConnection(bool initialize = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        bool needsSchema = initialize || !_initialized || !File.Exists(_databasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            if (needsSchema)
            {
                using SqliteCommand schema = connection.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS collection_gap_scopes (
                        scope_id INTEGER PRIMARY KEY,
                        provider TEXT NOT NULL,
                        symbol TEXT NOT NULL,
                        instrument_id TEXT NOT NULL,
                        session_date INTEGER NOT NULL,
                        session_bounds TEXT NOT NULL,
                        adjustment_policy TEXT NOT NULL,
                        adjustment_basis TEXT NOT NULL,
                        source_interval_seconds INTEGER NOT NULL,
                        has_returned_candles INTEGER NOT NULL DEFAULT 0,
                        UNIQUE(provider, symbol, instrument_id, session_date, session_bounds, adjustment_policy, adjustment_basis, source_interval_seconds)
                    );
                    CREATE TABLE IF NOT EXISTS collection_unavailable_ranges (
                        scope_id INTEGER NOT NULL REFERENCES collection_gap_scopes(scope_id),
                        from_ticks INTEGER NOT NULL,
                        through_ticks INTEGER NOT NULL CHECK (through_ticks > from_ticks),
                        retry_after_ticks INTEGER,
                        PRIMARY KEY(scope_id, from_ticks)
                    ) WITHOUT ROWID;
                    CREATE INDEX IF NOT EXISTS collection_unavailable_expiry
                        ON collection_unavailable_ranges(scope_id, retry_after_ticks) WHERE retry_after_ticks IS NOT NULL;
                    """;
                schema.ExecuteNonQuery();
                _initialized = true;
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private const string ScopePredicate = """
        provider = $provider AND symbol = $symbol AND instrument_id = $instrument AND session_date = $date
        AND session_bounds = $session AND adjustment_policy = $policy AND adjustment_basis = $basis AND source_interval_seconds = $interval
        """;

    private void AddScopeParameters(SqliteCommand command, CollectionGapKey key)
    {
        command.Parameters.AddWithValue("$provider", _providerIdentity);
        command.Parameters.AddWithValue("$symbol", key.Symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$instrument", key.InstrumentId ?? string.Empty);
        command.Parameters.AddWithValue("$date", key.SessionDate.DayNumber);
        command.Parameters.AddWithValue("$session", key.SessionBounds);
        command.Parameters.AddWithValue("$policy", key.AdjustmentPolicy);
        command.Parameters.AddWithValue("$basis", key.AdjustmentBasis);
        command.Parameters.AddWithValue("$interval", key.SourceIntervalSeconds);
    }

    private static void ValidateKeyAndRange(CollectionGapKey key, DateTimeOffset fromUtc, DateTimeOffset throughUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.SessionBounds);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.AdjustmentPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.AdjustmentBasis);
        if (key.SourceIntervalSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(key), "The source interval must be positive.");
        if (fromUtc >= throughUtc) throw new ArgumentException("The query or attempt must have positive duration.", nameof(throughUtc));
    }

    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    private sealed record Block(long FromTicks, long ThroughTicks, long? RetryAfterTicks);
}
