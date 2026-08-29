using Microsoft.Data.Sqlite;

namespace PriceSentinel3000.Infrastructure.Storage;

internal static class SqliteJournalSchema
{
    public static void Initialize(SqliteConnection connection)
    {
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
                open_price REAL NULL,
                high_price REAL NULL,
                low_price REAL NULL,
                close_price REAL NULL,
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

            CREATE TABLE IF NOT EXISTS live_order_events (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                client_reference_id TEXT NOT NULL,
                broker_order_id TEXT NULL,
                symbol TEXT NOT NULL,
                side TEXT NOT NULL,
                quantity REAL NOT NULL,
                state TEXT NOT NULL,
                details_json TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id)
            );

            CREATE INDEX IF NOT EXISTS ix_live_order_events_session_time
            ON live_order_events(session_id, occurred_at_utc);

            CREATE INDEX IF NOT EXISTS ix_live_order_events_reference
            ON live_order_events(client_reference_id, occurred_at_utc);

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
        EnsureQuoteCandleColumns(connection);
    }

    private static void EnsureQuoteCandleColumns(SqliteConnection connection)
    {
        using SqliteCommand inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(quotes);";
        using SqliteDataReader reader = inspect.ExecuteReader();
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            existingColumns.Add(reader.GetString(1));
        }

        reader.Close();

        foreach (string column in new[]
                 {
                     "open_price",
                     "high_price",
                     "low_price",
                     "close_price",
                 })
        {
            if (existingColumns.Contains(column))
            {
                continue;
            }

            using SqliteCommand alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE quotes ADD COLUMN {column} REAL NULL;";
            alter.ExecuteNonQuery();
        }

        using SqliteCommand version = connection.CreateCommand();
        version.CommandText =
            """
            INSERT OR IGNORE INTO schema_version(version, applied_at_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        version.ExecuteNonQuery();
    }
}
