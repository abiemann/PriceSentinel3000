using Microsoft.Data.Sqlite;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Infrastructure.Tests.Storage;

public sealed class SqliteTradingJournalTests
{
    [Fact]
    public void Initialize_MigratesLegacyQuotesAndPreservesTheirFifteenSecondDuration()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PriceSentinel3000.Tests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "legacy-journal.db");
        Directory.CreateDirectory(directory);

        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using SqliteCommand legacySchema = connection.CreateCommand();
                legacySchema.CommandText =
                    """
                    CREATE TABLE schema_version (
                        version INTEGER NOT NULL PRIMARY KEY,
                        applied_at_utc TEXT NOT NULL
                    );
                    INSERT INTO schema_version(version, applied_at_utc)
                    VALUES (1, '2026-08-01T00:00:00Z');
                    CREATE TABLE quotes (
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
                        ingestion_kind TEXT NOT NULL
                    );
                    INSERT INTO quotes (
                        session_id, symbol, asset_class, observed_at_utc, source_at_utc,
                        bid, ask, last, volume, ingestion_kind)
                    VALUES (
                        'b2022bca-345e-4bfa-a39c-dc2f850c9414', 'SOFI', 'Equity',
                        '2026-08-01T16:01:00Z', '2026-08-01T16:00:00Z',
                        9.99, 10.01, 10, 100, 'Replay');
                    """;
                legacySchema.ExecuteNonQuery();
            }

            using (var journal = new SqliteTradingJournal(databasePath))
            {
                journal.Initialize();
                journal.Initialize();
                MarketQuote migrated = Assert.Single(journal.ReadSessionQuotes(
                    Guid.Parse("b2022bca-345e-4bfa-a39c-dc2f850c9414"), new Instrument("SOFI")));
                Assert.Equal(15, migrated.SourceIntervalSeconds);
                Assert.Equal(new DateTimeOffset(2026, 8, 1, 16, 0, 0, TimeSpan.Zero), migrated.SourceTimestampUtc);
                Assert.Equal(migrated.SourceTimestampUtc.AddSeconds(15), migrated.SourceEndsAtUtc);
                Assert.Equal(10m, migrated.Last);
            }

            using var verification = new SqliteConnection($"Data Source={databasePath}");
            verification.Open();
            using SqliteCommand inspect = verification.CreateCommand();
            inspect.CommandText = "PRAGMA table_info(quotes);";
            using SqliteDataReader reader = inspect.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("open_price", columns);
            Assert.Contains("high_price", columns);
            Assert.Contains("low_price", columns);
            Assert.Contains("close_price", columns);
            Assert.Contains("source_interval_seconds", columns);
            reader.Close();
            inspect.CommandText = "SELECT COUNT(*) FROM schema_version WHERE version = 4;";
            Assert.Equal(1L, inspect.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void Journal_PreservesHistoricalDurationAndCandleAcrossReopen(int intervalSeconds)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "PriceSentinel3000.Tests", Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "journal.db");

        try
        {
            var instrument = new Instrument("NFLX");
            DateTimeOffset start = new(2026, 8, 24, 13, 30, 0, TimeSpan.Zero);
            MarketQuote source = Quote(instrument, start, 80m) with
            {
                ObservedAtUtc = start.AddDays(14),
                OpenPrice = 79.95m,
                HighPrice = 80.05m,
                LowPrice = 79.90m,
                ClosePrice = 80m,
                SourceIntervalSeconds = intervalSeconds,
            };
            Guid sessionId;
            using (var journal = new SqliteTradingJournal(databasePath))
            {
                journal.Initialize();
                sessionId = journal.StartSession(instrument, TradingMode.Replay, 1400m, "{}", start).Id;
                journal.AppendQuotes(sessionId, [source], QuoteIngestionKind.Replay);
            }

            using var reopened = new SqliteTradingJournal(databasePath);
            reopened.Initialize();
            MarketQuote restored = Assert.Single(reopened.ReadSessionQuotes(sessionId, instrument));
            Assert.Equal(source, restored);
            Assert.Equal(start.AddSeconds(intervalSeconds), restored.SourceEndsAtUtc);
            Assert.Equal(1, reopened.GetSummary(sessionId).QuoteCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Journal_PersistsSessionsQuotesActivitiesAndReplaySource()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PriceSentinel3000.Tests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "journal.db");

        try
        {
            using var journal = new SqliteTradingJournal(databasePath);
            journal.Initialize();
            var instrument = new Instrument("SOFI");
            DateTimeOffset start =
                new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
            JournalSession session = journal.StartSession(
                instrument,
                TradingMode.PaperTrader,
                10_000m,
                "{}",
                start);
            MarketQuote first = Quote(instrument, start, 10m) with
            {
                OpenPrice = 9.95m,
                HighPrice = 10.05m,
                LowPrice = 9.90m,
                ClosePrice = 10m,
            };
            MarketQuote second = Quote(instrument, start.AddSeconds(5), 10.1m);

            journal.AppendQuotes(
                session.Id,
                [first, second],
                QuoteIngestionKind.WarmStart);
            journal.AppendQuotes(
                session.Id,
                [first with { ObservedAtUtc = start.AddSeconds(45) }],
                QuoteIngestionKind.Reconciliation);
            journal.AppendActivity(session.Id, start, "INFO", "Session test.");
            var decision = new StrategyDecision(
                start.AddSeconds(10),
                StrategySignalKind.Buy,
                "BOTTOM CONFIRMED",
                0.8m,
                ["Test decision."],
                30m,
                0.05m,
                0.05m);
            journal.AppendDecision(session.Id, decision);
            var order = new PaperOrder(
                Guid.NewGuid(),
                start.AddSeconds(10),
                PaperOrderSide.Buy,
                2m,
                10.1m,
                decision.State);
            var fill = new PaperFill(
                order.Id,
                start.AddSeconds(10),
                PaperOrderSide.Buy,
                2m,
                10.1m,
                0m);
            var account = new PaperAccountSnapshot(
                9_979.8m,
                9_979.8m,
                10_000m,
                2m,
                10.1m,
                20.2m,
                0m,
                0m,
                1,
                false);
            journal.AppendPaperFill(session.Id, instrument, order, fill, account);
            journal.CompleteSession(session.Id, start.AddMinutes(1), "COMPLETED");

            JournalSummary summary = journal.GetSummary(session.Id);
            ReplaySourceSession? replaySource =
                journal.FindLatestReplaySource(instrument);
            IReadOnlyList<MarketQuote> replayQuotes =
                journal.ReadSessionQuotes(session.Id, instrument);

            Assert.True(File.Exists(databasePath));
            Assert.Equal(3, summary.QuoteCount);
            Assert.Equal(1, summary.ActivityCount);
            Assert.Equal(1, summary.DecisionCount);
            Assert.Equal(1, summary.OrderCount);
            Assert.Equal(1, summary.FillCount);
            Assert.NotNull(replaySource);
            Assert.Equal(session.Id, replaySource.Id);
            Assert.Equal(2, replayQuotes.Count);
            Assert.Equal(first.OpenPrice, replayQuotes[0].OpenPrice);
            Assert.Equal(first.HighPrice, replayQuotes[0].HighPrice);
            Assert.Equal(first.LowPrice, replayQuotes[0].LowPrice);
            Assert.Equal(first.ClosePrice, replayQuotes[0].ClosePrice);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static MarketQuote Quote(
        Instrument instrument,
        DateTimeOffset timestamp,
        decimal last) =>
        new(
            instrument,
            timestamp,
            timestamp,
            last - 0.01m,
            last + 0.01m,
            last,
            100m);
}
