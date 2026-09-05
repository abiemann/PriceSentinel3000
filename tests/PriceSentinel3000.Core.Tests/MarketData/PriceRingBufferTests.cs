using PriceSentinel3000.Core.Configuration;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Core.Tests.MarketData;

public sealed class PriceRingBufferTests
{
    private static readonly Instrument Instrument = new("SOFI");
    private static readonly DateTimeOffset Start =
        new(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_DeduplicatesVerificationAndAcceptsNewerCorrection()
    {
        var buffer = new PriceRingBuffer(Instrument, TimeSpan.FromMinutes(7));
        MarketQuote original = Quote(Start, 10m, Start);
        MarketQuote duplicate = Quote(Start, 10m, Start.AddSeconds(45));
        MarketQuote correction = Quote(Start, 10.05m, Start.AddSeconds(46));

        QuoteMergeResult first = buffer.Merge([original]);
        QuoteMergeResult second = buffer.Merge([duplicate]);
        QuoteMergeResult third = buffer.Merge([correction]);

        Assert.Equal(1, first.Added);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(1, third.Corrected);
        Assert.Single(buffer.Snapshot());
        Assert.Equal(10.05m, buffer.Snapshot()[0].Last);
    }

    [Fact]
    public void Merge_TrimsQuotesOutsideRetentionWindow()
    {
        var buffer = new PriceRingBuffer(Instrument, TimeSpan.FromMinutes(2));

        buffer.Merge(
        [
            Quote(Start, 10m, Start),
            Quote(Start.AddMinutes(1), 10.1m, Start.AddMinutes(1)),
            Quote(Start.AddMinutes(3), 10.2m, Start.AddMinutes(3)),
        ]);

        IReadOnlyList<MarketQuote> snapshot = buffer.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(Start.AddMinutes(1), snapshot[0].SourceTimestampUtc);
    }

    [Fact]
    public void MinimumObservations_KeepSparsePollingStrategyWarmAfterInitialHistoryExpires()
    {
        TradingSessionSettings settings = TradingSessionSettings.Default with
        {
            BufferMinutes = 5,
            QuotePollingSeconds = 60,
            ReconciliationSeconds = 300,
            ReconciliationLookbackSeconds = 60,
            ReconciliationCompletionDelaySeconds = 30,
        };
        Assert.Empty(TradingSessionSettingsValidator.Validate(settings));
        var buffer = new PriceRingBuffer(
            Instrument,
            TimeSpan.FromMinutes(settings.BufferMinutes),
            minimumObservationCount: PriceActionSignalEngine.RsiPeriod + 2);
        var strategy = new PriceActionSignalEngine(settings.BufferMinutes);
        buffer.Merge(Enumerable.Range(-20, 21).Select(index =>
            Quote(Start.AddSeconds(index * 15), 10m, Start)));

        for (int minute = 1; minute <= 30; minute++)
        {
            DateTimeOffset now = Start.AddMinutes(minute);
            buffer.Merge([Quote(now, 10m, now)]);

            if (minute % 5 == 0)
            {
                buffer.Merge(Enumerable.Range(0, 5).Select(index =>
                    Quote(now.AddSeconds(-90 + index * 15), 10m, now)));
            }

            IReadOnlyList<MarketQuote> snapshot = buffer.Snapshot();
            StrategyDecision decision = strategy.Evaluate(snapshot, StrategyPositionContext.Flat);
            Assert.NotNull(decision.SimpleRsi);
            Assert.NotEqual("WARMING UP", decision.State);

            if (minute >= 10)
            {
                Assert.Equal(PriceActionSignalEngine.RsiPeriod + 2, snapshot.Count);
            }
        }
    }

    [Fact]
    public void Merge_AcceptsHistoricalBarWithoutBidAskBook()
    {
        var buffer = new PriceRingBuffer(Instrument, TimeSpan.FromMinutes(7));
        var historicalBar = new MarketQuote(
            Instrument,
            Start.AddHours(1),
            Start,
            0m,
            0m,
            10m,
            1_000m);

        QuoteMergeResult result = buffer.Merge([historicalBar]);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Rejected);
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public void Merge_RejectsMalformedOneSidedMarket()
    {
        var buffer = new PriceRingBuffer(Instrument, TimeSpan.FromMinutes(7));
        var malformed = new MarketQuote(
            Instrument,
            Start,
            Start,
            10m,
            0m,
            10m,
            100m);

        QuoteMergeResult result = buffer.Merge([malformed]);

        Assert.Equal(1, result.Rejected);
        Assert.Empty(buffer.Snapshot());
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 0, -1, false)]
    [InlineData(11, 10, 10, false)]
    [InlineData(10, 0, 10, false)]
    [InlineData(0, 10, 10, false)]
    [InlineData(-1, 10, 10, false)]
    [InlineData(0, 0, 10, true)]
    [InlineData(10, 10, 10, true)]
    public void IsValidQuote_UsesTheSamePriceRulesAsMerge(
        decimal bid,
        decimal ask,
        decimal last,
        bool expected)
    {
        var buffer = new PriceRingBuffer(Instrument, TimeSpan.FromMinutes(7));
        var quote = new MarketQuote(Instrument, Start, Start, bid, ask, last, 0m);

        Assert.Equal(expected, buffer.IsValidQuote(quote));
        Assert.Equal(expected ? 1 : 0, buffer.Merge([quote]).Added);
    }

    [Fact]
    public void IsValidQuote_RejectsAnotherInstrumentAndMalformedCandles()
    {
        var buffer = new PriceRingBuffer(Instrument, TimeSpan.FromMinutes(7));
        MarketQuote quote = Quote(Start, 10m, Start);

        Assert.False(buffer.IsValidQuote(quote with { Instrument = new("SPY") }));
        Assert.False(buffer.IsValidQuote(quote with { OpenPrice = 10m }));
        Assert.False(buffer.IsValidQuote(quote with
        {
            OpenPrice = 10m,
            HighPrice = 9m,
            LowPrice = 8m,
            ClosePrice = 10m,
        }));
        Assert.False(buffer.IsValidQuote(quote with
        {
            OpenPrice = 10m,
            HighPrice = 12m,
            LowPrice = 11m,
            ClosePrice = 10m,
        }));
    }

    [Fact]
    public void MinuteAnalyzer_ReportsEachSerialBlockIndependently()
    {
        MarketQuote[] quotes =
        [
            Quote(Start.AddSeconds(10), 10m, Start.AddSeconds(10)),
            Quote(Start.AddSeconds(50), 10.1m, Start.AddSeconds(50)),
            Quote(Start.AddSeconds(70), 10.2m, Start.AddSeconds(70)),
            Quote(Start.AddSeconds(120), 10m, Start.AddSeconds(120)),
        ];

        IReadOnlyList<MinuteBlock> blocks = MinuteBlockAnalyzer.Analyze(
            quotes,
            2,
            Start.AddMinutes(2));

        Assert.Equal(PriceDirection.Up, blocks[0].Direction);
        Assert.Equal(PriceDirection.Down, blocks[1].Direction);
        Assert.Equal(2, blocks[0].QuoteCount);
        Assert.Equal(2, blocks[1].QuoteCount);
    }

    private static MarketQuote Quote(
        DateTimeOffset sourceTimestamp,
        decimal last,
        DateTimeOffset observedAt) =>
        new(
            Instrument,
            observedAt,
            sourceTimestamp,
            last - 0.01m,
            last + 0.01m,
            last,
            100m);
}
