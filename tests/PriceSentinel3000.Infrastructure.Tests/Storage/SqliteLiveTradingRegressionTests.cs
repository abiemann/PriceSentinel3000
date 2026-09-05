using Microsoft.Data.Sqlite;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Infrastructure.Tests.Storage;

public sealed class SqliteLiveTradingRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "PriceSentinel3000.Tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "journal.db");

    [Fact]
    public void DistinctExecutionIdsWithIdenticalEconomicsAreBothPersisted()
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        var (sessionId, instrument, intent, order) = CreateFilledOrder(journal);

        journal.AppendLiveOrderEvent(
            sessionId, instrument, "TERMINAL", intent, null, order, order.UpdatedAtUtc);
        journal.AppendLiveOrderEvent(
            sessionId, instrument, "TERMINAL", intent, null, order, order.UpdatedAtUtc);

        Assert.Equal(2, journal.GetSummary(sessionId).FillCount);
        Assert.Equal(2d, Scalar("SELECT SUM(quantity) FROM fills;"));
        Assert.Equal(2L, Scalar("SELECT COUNT(DISTINCT execution_id) FROM fills;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyExecutionsAreRecoveredWithoutDuplicatingExistingFills(bool retainEvents)
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        var (sessionId, instrument, intent, order) = CreateFilledOrder(journal);
        journal.AppendLiveOrderEvent(
            sessionId, instrument, "TERMINAL", intent, null, order, order.UpdatedAtUtc);
        journal.AppendLiveOrderEvent(
            sessionId, instrument, "TERMINAL", intent, null, order, order.UpdatedAtUtc);

        // A fill without retained broker details must survive migration unchanged.
        Execute(
            """
            INSERT INTO fills(order_id, filled_at_utc, quantity, price, fees)
            SELECT id, '2026-09-04T14:00:00.0000000+00:00', 3, 9, 0 FROM orders;
            """);

        // Restore the pre-migration schema and its collapsed same-economics fill.
        Execute(
            """
            DROP INDEX ix_fills_order_execution;
            DELETE FROM fills WHERE quantity = 1 AND id <> (SELECT MIN(id) FROM fills);
            ALTER TABLE fills DROP COLUMN execution_id;
            DELETE FROM schema_version WHERE version = 3;
            """);
        if (!retainEvents)
        {
            Execute("DELETE FROM live_order_events;");
        }

        using var reopened = new SqliteTradingJournal(DatabasePath);
        reopened.Initialize();
        Assert.Equal(retainEvents ? 3 : 2, reopened.GetSummary(sessionId).FillCount);
        reopened.Initialize();
        reopened.AppendLiveOrderEvent(
            sessionId, instrument, "TERMINAL", intent, null, order, order.UpdatedAtUtc);
        reopened.AppendLiveOrderEvent(
            sessionId, instrument, "TERMINAL", intent, null, order, order.UpdatedAtUtc);

        Assert.Equal(3, reopened.GetSummary(sessionId).FillCount);
        Assert.Equal(5d, Scalar("SELECT SUM(quantity) FROM fills;"));
        Assert.Equal(2L, Scalar("SELECT COUNT(DISTINCT execution_id) FROM fills;"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM fills WHERE execution_id IS NULL;"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM schema_version WHERE version = 3;"));
    }

    [Fact]
    public void DailyBaselinesSurviveRestartAndAreScopedToAccountAndTradingDay()
    {
        DateTimeOffset day = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
        using (var journal = new SqliteTradingJournal(DatabasePath))
        {
            journal.StartSession(new Instrument("SOFI"), TradingMode.Live,
                50_000m, "{}", day.AddHours(1));
            Assert.Null(journal.GetLiveDailyStartingBalance("account-a", day));
            Assert.Equal(10_000.123456789m,
                journal.GetOrCreateLiveDailyStartingBalance("account-a", day, 10_000.123456789m));
            Assert.Equal(10_000.123456789m,
                journal.GetOrCreateLiveDailyStartingBalance("account-a", day, 9_000m));
            Assert.Equal(500m,
                journal.GetOrCreateLiveDailyStartingBalance("account-b", day, 500m));
            Assert.Equal(9_500m,
                journal.GetOrCreateLiveDailyStartingBalance("account-a", day.AddDays(1), 9_500m));
        }

        using var reopened = new SqliteTradingJournal(DatabasePath);
        Assert.Equal(10_000.123456789m,
            reopened.GetLiveDailyStartingBalance("account-a", day.ToOffset(TimeSpan.FromHours(-4))));
        Assert.Equal(500m, reopened.GetLiveDailyStartingBalance("account-b", day));
        Assert.Equal(9_500m, reopened.GetLiveDailyStartingBalance("account-a", day.AddDays(1)));
        Assert.Null(reopened.GetLiveDailyStartingBalance("account-c", day));
    }

    [Fact]
    public void LegacyGeneratedIdsFromDifferentSnapshotsDoNotMultiplyOneFill()
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        var (sessionId, instrument, intent, order) = CreateFilledOrder(journal);
        BrokerOrderSnapshot first = order with
        {
            FilledQuantity = 1m,
            Executions = [new(Guid.NewGuid().ToString("D"), order.UpdatedAtUtc, 1m, 10m)],
        };
        BrokerOrderSnapshot second = first with
        {
            Executions = [new(Guid.NewGuid().ToString("D"), order.UpdatedAtUtc, 1m, 10m)],
        };
        journal.AppendLiveOrderEvent(
            sessionId, instrument, "BROKER_STATE", intent, null, first, order.UpdatedAtUtc);
        journal.AppendLiveOrderEvent(
            sessionId, instrument, "BROKER_STATE", intent, null, second, order.UpdatedAtUtc.AddSeconds(1));
        Execute(
            """
            DROP INDEX ix_fills_order_execution;
            DELETE FROM fills WHERE id <> (SELECT MIN(id) FROM fills);
            ALTER TABLE fills DROP COLUMN execution_id;
            DELETE FROM schema_version WHERE version = 3;
            """);

        using var reopened = new SqliteTradingJournal(DatabasePath);
        reopened.Initialize();
        reopened.Initialize();

        Assert.Equal(1, reopened.GetSummary(sessionId).FillCount);
        Assert.Equal(1d, Scalar("SELECT SUM(quantity) FROM fills;"));
        Assert.Equal(second.Executions[0].Id, Scalar("SELECT execution_id FROM fills;"));
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"LiveAccountNumber\":null}", true)]
    [InlineData("{\"LiveAccountNumber\":\" \"}", true)]
    [InlineData("{\"LiveAccountNumber\":123}", true)]
    [InlineData("invalid json", true)]
    [InlineData("{\"LiveAccountNumber\":\"account-a\"}", false)]
    public void UnattributedLiveSessionsRequireAnExplicitAccountNumber(string settings, bool expected)
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        DateTimeOffset day = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
        journal.StartSession(new Instrument("SOFI"), TradingMode.Live, 10_000m, settings, day);

        Assert.Equal(expected, journal.HasUnattributedLiveSessionsSince(day));
        Assert.False(journal.HasUnattributedLiveSessionsSince(day.AddDays(1)));
    }

    [Fact]
    public void LegacyBlankExecutionIdDoesNotPreventJournalInitialization()
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        var (sessionId, instrument, intent, order) = CreateFilledOrder(journal);
        order = order with { FilledQuantity = 1m, Executions = [order.Executions[0]] };
        journal.AppendLiveOrderEvent(
            sessionId, instrument, "BROKER_STATE", intent, null, order, order.UpdatedAtUtc);
        Execute(
            """
            UPDATE live_order_events
            SET details_json = json_set(details_json, '$.Order.Executions[0].Id', '');
            DROP INDEX ix_fills_order_execution;
            ALTER TABLE fills DROP COLUMN execution_id;
            DELETE FROM schema_version WHERE version = 3;
            """);

        using var reopened = new SqliteTradingJournal(DatabasePath);
        reopened.Initialize();
        Assert.Equal(1, reopened.GetSummary(sessionId).FillCount);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM fills WHERE execution_id IS NULL;"));
        reopened.AppendLiveOrderEvent(
            sessionId, instrument, "BROKER_STATE", intent, null, order, order.UpdatedAtUtc);
        Assert.Equal(1, reopened.GetSummary(sessionId).FillCount);
        Assert.Equal(order.Executions[0].Id, Scalar("SELECT execution_id FROM fills;"));
    }

    [Fact]
    public void OldAndPaperSessionsDoNotBlockAnAccountScopedDailyBaseline()
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        DateTimeOffset day = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);
        journal.StartSession(new Instrument("SOFI"), TradingMode.Live, 10_000m, "{}", day.AddMinutes(-1));
        journal.StartSession(new Instrument("SOFI"), TradingMode.PaperTrader, 10_000m, "{}", day.AddHours(1));
        journal.StartSession(new Instrument("SOFI"), TradingMode.Live, 10_000m,
            "{\"LiveAccountNumber\":\"account-b\"}", day.AddHours(1));

        Assert.False(journal.HasUnattributedLiveSessionsSince(day));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DailyBaselineRequiresPositiveEquity(decimal balance)
    {
        using var journal = new SqliteTradingJournal(DatabasePath);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            journal.GetOrCreateLiveDailyStartingBalance("account-a", DateTimeOffset.UtcNow, balance));
    }

    private static (Guid SessionId, Instrument Instrument, BrokerOrderIntent Intent,
        BrokerOrderSnapshot Order) CreateFilledOrder(SqliteTradingJournal journal)
    {
        var instrument = new Instrument("SOFI");
        DateTimeOffset now = new(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);
        Guid sessionId = journal.StartSession(
            instrument, TradingMode.Live, 10_000m, "{}", now).Id;
        var intent = new BrokerOrderIntent(
            Guid.NewGuid(), now, "SOFI", BrokerOrderSide.Buy, 2m, "TEST");
        var order = new BrokerOrderSnapshot(
            intent.ClientReferenceId, "broker-order", "SOFI", BrokerOrderSide.Buy,
            BrokerOrderState.Filled, 2m, 2m, 10m, null, now,
            [new("execution-1", now, 1m, 10m), new("execution-2", now, 1m, 10m)]);
        return (sessionId, instrument, intent, order);
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
