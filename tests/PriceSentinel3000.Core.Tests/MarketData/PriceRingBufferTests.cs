using PriceSentinel3000.Core.MarketData;

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
