using Microsoft.Data.Sqlite;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;
using PriceSentinel3000.Infrastructure.Storage;

namespace PriceSentinel3000.Core.Tests.Storage;

public sealed class SqliteTradingJournalTests
{
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
            MarketQuote first = Quote(instrument, start, 10m);
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
