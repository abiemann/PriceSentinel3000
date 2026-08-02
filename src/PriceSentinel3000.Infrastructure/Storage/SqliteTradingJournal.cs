using System.Globalization;
using Microsoft.Data.Sqlite;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;

namespace PriceSentinel3000.Infrastructure.Storage;

public sealed class SqliteTradingJournal : ITradingJournal
{
    private bool _initialized;

    public SqliteTradingJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        pragmas.ExecuteNonQuery();

        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_version(version, applied_at_utc)
            VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT NOT NULL PRIMARY KEY,
                symbol TEXT NOT NULL,
                asset_class TEXT NOT NULL,
                mode TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT NULL,
                starting_balance REAL NOT NULL,
                settings_json TEXT NOT NULL,
                outcome TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS quotes (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                symbol TEXT NOT NULL,
                asset_class TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                source_at_utc TEXT NOT NULL,
                bid REAL NOT NULL,
                ask REAL NOT NULL,
                last REAL NOT NULL,
                volume REAL NOT NULL,
                ingestion_kind TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE INDEX IF NOT EXISTS ix_quotes_symbol_source
            ON quotes(symbol, source_at_utc);

            CREATE INDEX IF NOT EXISTS ix_quotes_session_source
            ON quotes(session_id, source_at_utc);

            CREATE TABLE IF NOT EXISTS activities (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NULL,
                occurred_at_utc TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE INDEX IF NOT EXISTS ix_activities_session_time
            ON activities(session_id, occurred_at_utc);

            CREATE TABLE IF NOT EXISTS decisions (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                evaluated_at_utc TEXT NOT NULL,
                state TEXT NOT NULL,
                signal TEXT NOT NULL,
                confidence REAL NULL,
                reasons_json TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE TABLE IF NOT EXISTS orders (
                id TEXT NOT NULL PRIMARY KEY,
                session_id TEXT NOT NULL,
                submitted_at_utc TEXT NOT NULL,
                side TEXT NOT NULL,
                quantity REAL NOT NULL,
                order_type TEXT NOT NULL,
                limit_price REAL NULL,
                status TEXT NOT NULL,
                broker_order_id TEXT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE TABLE IF NOT EXISTS fills (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                order_id TEXT NOT NULL,
                filled_at_utc TEXT NOT NULL,
                quantity REAL NOT NULL,
                price REAL NOT NULL,
                fees REAL NOT NULL DEFAULT 0,
                FOREIGN KEY(order_id) REFERENCES orders(id)
            );

            CREATE TABLE IF NOT EXISTS positions (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                symbol TEXT NOT NULL,
                quantity REAL NOT NULL,
                average_price REAL NOT NULL,
                market_value REAL NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE TABLE IF NOT EXISTS errors (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NULL,
                occurred_at_utc TEXT NOT NULL,
                component TEXT NOT NULL,
                message TEXT NOT NULL,
                details TEXT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE TABLE IF NOT EXISTS settings_snapshots (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                settings_json TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE TABLE IF NOT EXISTS zones (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                detected_at_utc TEXT NOT NULL,
                kind TEXT NOT NULL,
                lower_price REAL NOT NULL,
                upper_price REAL NOT NULL,
                evidence_json TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );
            """;
        schema.ExecuteNonQuery();
        _initialized = true;
    }

    public JournalSession StartSession(
        Instrument instrument,
        TradingMode mode,
        decimal startingBalance,
        string settingsJson,
        DateTimeOffset startedAtUtc)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(settingsJson);

        var session = new JournalSession(
            Guid.NewGuid(),
            instrument,
            mode,
            startedAtUtc.ToUniversalTime(),
            startingBalance,
            settingsJson);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions(
                id, symbol, asset_class, mode, started_at_utc,
                starting_balance, settings_json)
            VALUES(
                $id, $symbol, $asset_class, $mode, $started_at_utc,
                $starting_balance, $settings_json);
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$symbol", instrument.Symbol);
        command.Parameters.AddWithValue("$asset_class", instrument.AssetClass.ToString());
        command.Parameters.AddWithValue("$mode", mode.ToString());
        command.Parameters.AddWithValue("$started_at_utc", Format(startedAtUtc));
        command.Parameters.AddWithValue("$starting_balance", (double)startingBalance);
        command.Parameters.AddWithValue("$settings_json", settingsJson);
        command.Prepare();
        command.ExecuteNonQuery();
        return session;
    }

    public void AppendQuotes(
        Guid sessionId,
        IEnumerable<MarketQuote> quotes,
        QuoteIngestionKind ingestionKind)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(quotes);
        MarketQuote[] batch = [.. quotes];

        if (batch.Length == 0)
        {
            return;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO quotes(
                session_id, symbol, asset_class, observed_at_utc,
                source_at_utc, bid, ask, last, volume, ingestion_kind)
            VALUES(
                $session_id, $symbol, $asset_class, $observed_at_utc,
                $source_at_utc, $bid, $ask, $last, $volume, $ingestion_kind);
            """;
        SqliteParameter sessionParameter = command.Parameters.Add("$session_id", SqliteType.Text);
        SqliteParameter symbolParameter = command.Parameters.Add("$symbol", SqliteType.Text);
        SqliteParameter assetClassParameter = command.Parameters.Add("$asset_class", SqliteType.Text);
        SqliteParameter observedParameter = command.Parameters.Add("$observed_at_utc", SqliteType.Text);
        SqliteParameter sourceParameter = command.Parameters.Add("$source_at_utc", SqliteType.Text);
        SqliteParameter bidParameter = command.Parameters.Add("$bid", SqliteType.Real);
        SqliteParameter askParameter = command.Parameters.Add("$ask", SqliteType.Real);
        SqliteParameter lastParameter = command.Parameters.Add("$last", SqliteType.Real);
        SqliteParameter volumeParameter = command.Parameters.Add("$volume", SqliteType.Real);
        SqliteParameter kindParameter = command.Parameters.Add("$ingestion_kind", SqliteType.Text);
        command.Prepare();

        foreach (MarketQuote quote in batch)
        {
            sessionParameter.Value = sessionId.ToString("D");
            symbolParameter.Value = quote.Instrument.Symbol;
            assetClassParameter.Value = quote.Instrument.AssetClass.ToString();
            observedParameter.Value = Format(quote.ObservedAtUtc);
            sourceParameter.Value = Format(quote.SourceTimestampUtc);
            bidParameter.Value = (double)quote.Bid;
            askParameter.Value = (double)quote.Ask;
            lastParameter.Value = (double)quote.Last;
            volumeParameter.Value = (double)quote.Volume;
            kindParameter.Value = ingestionKind.ToString();
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void AppendActivity(
        Guid? sessionId,
        DateTimeOffset occurredAtUtc,
        string level,
        string message)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO activities(session_id, occurred_at_utc, level, message)
            VALUES($session_id, $occurred_at_utc, $level, $message);
            """;
        command.Parameters.AddWithValue(
            "$session_id",
            sessionId is null ? DBNull.Value : sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$occurred_at_utc", Format(occurredAtUtc));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$message", message);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public void CompleteSession(Guid sessionId, DateTimeOffset endedAtUtc, string outcome)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE sessions
            SET ended_at_utc = $ended_at_utc, outcome = $outcome
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$ended_at_utc", Format(endedAtUtc));
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public JournalSummary GetSummary(Guid sessionId)
    {
        EnsureInitialized();

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM quotes WHERE session_id = $session_id),
                (SELECT COUNT(*) FROM activities WHERE session_id = $session_id);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        command.Prepare();

        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt32(0), reader.GetInt32(1));
    }

    public ReplaySourceSession? FindLatestReplaySource(Instrument instrument)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.started_at_utc, COUNT(q.id)
            FROM sessions AS s
            INNER JOIN quotes AS q ON q.session_id = s.id
            WHERE s.symbol = $symbol
              AND s.asset_class = $asset_class
              AND s.mode = 'PaperTrader'
            GROUP BY s.id, s.started_at_utc
            ORDER BY s.started_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$symbol", instrument.Symbol);
        command.Parameters.AddWithValue("$asset_class", instrument.AssetClass.ToString());
        command.Prepare();

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new(
            Guid.Parse(reader.GetString(0)),
            instrument,
            DateTimeOffset.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            reader.GetInt32(2));
    }

    public IReadOnlyList<MarketQuote> ReadSessionQuotes(
        Guid sourceSessionId,
        Instrument instrument)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT q.observed_at_utc, q.source_at_utc, q.bid, q.ask, q.last, q.volume
            FROM quotes AS q
            INNER JOIN (
                SELECT source_at_utc, MAX(id) AS latest_id
                FROM quotes
                WHERE session_id = $session_id
                GROUP BY source_at_utc
            ) AS latest ON latest.latest_id = q.id
            ORDER BY q.source_at_utc;
            """;
        command.Parameters.AddWithValue("$session_id", sourceSessionId.ToString("D"));
        command.Prepare();

        using SqliteDataReader reader = command.ExecuteReader();
        var quotes = new List<MarketQuote>();

        while (reader.Read())
        {
            quotes.Add(new(
                instrument,
                DateTimeOffset.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Convert.ToDecimal(reader.GetDouble(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(5), CultureInfo.InvariantCulture)));
        }

        return quotes;
    }

    public void Dispose()
    {
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using SqliteCommand foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
