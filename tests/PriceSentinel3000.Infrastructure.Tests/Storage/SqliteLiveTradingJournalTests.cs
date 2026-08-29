using Microsoft.Data.Sqlite;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Infrastructure.Tests.Storage;

public sealed class SqliteLiveTradingJournalTests
{
    [Fact]
    public void LiveOrderEvents_UpsertOrderAndPersistExecutionOnlyOnce()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PriceSentinel3000.Tests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "live-journal.db");

        try
        {
            using var journal = new SqliteTradingJournal(databasePath);
            journal.Initialize();
            var instrument = new Instrument("SOFI");
            DateTimeOffset now = new(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);
            JournalSession session = journal.StartSession(
                instrument,
                TradingMode.Live,
                10_000m,
                "{}",
                now);
            var intent = new BrokerOrderIntent(
                Guid.NewGuid(),
                now,
                "SOFI",
                BrokerOrderSide.Buy,
                2m,
                "BOTTOM CONFIRMED");
            var review = new BrokerOrderReview(
                intent,
                true,
                [],
                10m,
                10.02m,
                10.01m,
                "Bid $10.00 · Ask $10.02 · Last $10.01.",
                "{}");
            var execution = new BrokerExecution(
                "execution-1",
                now.AddSeconds(2),
                2m,
                10.02m);
            var order = new BrokerOrderSnapshot(
                intent.ClientReferenceId,
                "broker-order-1",
                "SOFI",
                BrokerOrderSide.Buy,
                BrokerOrderState.Filled,
                2m,
                2m,
                10.02m,
                null,
                now.AddSeconds(2),
                [execution]);

            journal.AppendLiveOrderEvent(
                session.Id, instrument, "INTENT_CREATED", intent, null, null, now);
            journal.AppendLiveOrderEvent(
                session.Id, instrument, "REVIEW_ACCEPTED", intent, review, null, now.AddSeconds(1));
            journal.AppendLiveOrderEvent(
                session.Id, instrument, "TERMINAL", intent, review, order, now.AddSeconds(2));
            journal.AppendLiveOrderEvent(
                session.Id, instrument, "TERMINAL", intent, review, order, now.AddSeconds(3));

            JournalSummary summary = journal.GetSummary(session.Id);

            Assert.Equal(1, summary.OrderCount);
            Assert.Equal(1, summary.FillCount);
            Assert.Equal(4, CountRows(databasePath, "live_order_events"));
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

    [Fact]
    public void GetLiveStartingBalanceSince_ReturnsEarliestLiveBaseline()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PriceSentinel3000.Tests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "daily-baseline.db");

        try
        {
            using var journal = new SqliteTradingJournal(databasePath);
            journal.Initialize();
            var instrument = new Instrument("SOFI");
            DateTimeOffset dayStart = new(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
            journal.StartSession(
                instrument,
                TradingMode.Live,
                10_000m,
                "{}",
                dayStart.AddHours(5));
            journal.StartSession(
                instrument,
                TradingMode.Live,
                9_950m,
                "{}",
                dayStart.AddHours(6));

            Assert.Equal(10_000m, journal.GetLiveStartingBalanceSince(dayStart));
            Assert.Null(journal.GetLiveStartingBalanceSince(dayStart.AddDays(1)));
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

    private static long CountRows(string databasePath, string table)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
