using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Journaling;

public enum QuoteIngestionKind
{
    WarmStart,
    Live,
    Reconciliation,
    Replay,
}

public sealed record JournalSession(
    Guid Id,
    Instrument Instrument,
    TradingMode Mode,
    DateTimeOffset StartedAtUtc,
    decimal StartingBalance,
    string SettingsJson);

public sealed record JournalSummary(
    int QuoteCount,
    int ActivityCount,
    int DecisionCount,
    int OrderCount,
    int FillCount);

public sealed record ReplaySourceSession(
    Guid Id,
    Instrument Instrument,
    DateTimeOffset StartedAtUtc,
    int QuoteCount);

public interface ITradingJournal : IDisposable
{
    string DatabasePath { get; }

    void Initialize();

    JournalSession StartSession(
        Instrument instrument,
        TradingMode mode,
        decimal startingBalance,
        string settingsJson,
        DateTimeOffset startedAtUtc);

    void AppendQuotes(
        Guid sessionId,
        IEnumerable<MarketQuote> quotes,
        QuoteIngestionKind ingestionKind);

    void AppendActivity(
        Guid? sessionId,
        DateTimeOffset occurredAtUtc,
        string level,
        string message);

    void AppendDecision(Guid sessionId, StrategyDecision decision);

    void AppendPaperFill(
        Guid sessionId,
        Instrument instrument,
        PaperOrder order,
        PaperFill fill,
        PaperAccountSnapshot account);

    void AppendLiveOrderEvent(
        Guid sessionId,
        Instrument instrument,
        string eventType,
        BrokerOrderIntent intent,
        BrokerOrderReview? review,
        BrokerOrderSnapshot? order,
        DateTimeOffset occurredAtUtc);

    decimal? GetLiveStartingBalanceSince(
        DateTimeOffset startedAtGteUtc);

    bool HasUnattributedLiveSessionsSince(DateTimeOffset startedAtGteUtc);

    decimal? GetLiveDailyStartingBalance(
        string accountNumber,
        DateTimeOffset tradingDayStartUtc);

    decimal GetOrCreateLiveDailyStartingBalance(
        string accountNumber,
        DateTimeOffset tradingDayStartUtc,
        decimal startingBalance);

    void CompleteSession(Guid sessionId, DateTimeOffset endedAtUtc, string outcome);

    JournalSummary GetSummary(Guid sessionId);

    ReplaySourceSession? FindLatestReplaySource(Instrument instrument);

    IReadOnlyList<MarketQuote> ReadSessionQuotes(
        Guid sourceSessionId,
        Instrument instrument);
}
