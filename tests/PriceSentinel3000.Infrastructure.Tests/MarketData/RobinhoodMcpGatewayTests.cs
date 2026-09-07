using System.Text.Json;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Infrastructure.MarketData;

namespace PriceSentinel3000.Infrastructure.Tests.MarketData;

public sealed class RobinhoodMcpGatewayTests
{
    private static readonly Instrument Instrument = new("NFLX", AssetClass.Equity);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-24T06:30:00-07:00");
    private static readonly DateTimeOffset End = Start.AddMinutes(3);

    [Fact]
    public void EquityHistoricalBounds_IncludesOvernightTrading()
    {
        Assert.Equal(
            "24_5",
            RobinhoodMcpGateway.EquityHistoricalBounds);
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(30, 2)]
    [InlineData(60, 3)]
    public async Task ReplayHistory_UsesFirstUsableSupportedIntervalAndKeepsRequestRange(
        int availableSeconds,
        int expectedCalls)
    {
        var requested = new List<string>();
        IReadOnlyList<MarketQuote> quotes = await RobinhoodMcpGateway.FetchReplayHistoryAsync(
            Instrument, Start, End, End.AddDays(10), (arguments, cancellationToken) =>
            {
                Assert.Equal(Start.ToUniversalTime().ToString("O"), arguments["start_time"]);
                Assert.Equal(End.ToUniversalTime().ToString("O"), arguments["end_time"]);
                Assert.Equal("24_5", arguments["bounds"]);
                Assert.Equal("split", arguments["adjustment_type"]);
                Assert.Equal(new[] { "NFLX" }, Assert.IsType<string[]>(arguments["symbols"]));
                string interval = Assert.IsType<string>(arguments["interval"]);
                requested.Add(interval);
                int seconds = IntervalSeconds(interval);
                return Task.FromResult(Response(interval,
                    Bar(Start, interpolated: seconds < availableSeconds)));
            }, CancellationToken.None);

        Assert.Equal(new[] { "15second", "30second", "minute" }.Take(expectedCalls), requested);
        MarketQuote quote = Assert.Single(quotes);
        Assert.Equal(availableSeconds, quote.SourceIntervalSeconds);
        Assert.Equal(Start.ToUniversalTime(), quote.SourceTimestampUtc);
        Assert.Equal(Start.AddSeconds(availableSeconds).ToUniversalTime(), quote.SourceEndsAtUtc);
        Assert.Equal(79.89m, quote.CandleOpen);
        Assert.Equal(80.0133m, quote.CandleHigh);
        Assert.Equal(79.17m, quote.CandleLow);
        Assert.Equal(79.24m, quote.CandleClose);
        Assert.Equal(370827m, quote.Volume);
    }

    [Fact]
    public async Task ReplayHistory_DoesNotReplaceSparseUsableFineHistoryWithCoarserBars()
    {
        int calls = 0;
        IReadOnlyList<MarketQuote> quotes = await RobinhoodMcpGateway.FetchReplayHistoryAsync(
            Instrument, Start, End, End, (_, _) =>
            {
                calls++;
                return Task.FromResult(Response("15second",
                    Bar(Start), Bar(Start.AddSeconds(15), interpolated: true),
                    Bar(Start.AddMinutes(2))));
            }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(2, quotes.Count);
        Assert.All(quotes, quote => Assert.Equal(15, quote.SourceIntervalSeconds));
    }

    [Fact]
    public async Task ReplayHistory_FallsBackWhenAllBarsAreOutsideRangeOrIncomplete()
    {
        var requested = new List<string>();
        IReadOnlyList<MarketQuote> quotes = await RobinhoodMcpGateway.FetchReplayHistoryAsync(
            Instrument, Start, End, End, (arguments, _) =>
            {
                string interval = Assert.IsType<string>(arguments["interval"]);
                requested.Add(interval);
                return Task.FromResult(interval == "minute"
                    ? Response(interval, Bar(Start.AddSeconds(-30)), Bar(Start),
                        Bar(End.AddSeconds(-30)), Bar(End))
                    : Response(interval, Bar(Start.AddSeconds(-15)),
                        Bar(End.AddSeconds(-5)), Bar(End)));
            }, CancellationToken.None);

        Assert.Equal(new[] { "15second", "30second", "minute" }, requested);
        MarketQuote quote = Assert.Single(quotes);
        Assert.Equal(Start.ToUniversalTime(), quote.SourceTimestampUtc);
        Assert.Equal(60, quote.SourceIntervalSeconds);
    }

    [Fact]
    public async Task ReplayHistory_FutureRequestedEndDoesNotExposeUnfinishedSourceBar()
    {
        DateTimeOffset observedAt = Start.AddSeconds(70);
        var requested = new List<string>();
        IReadOnlyList<MarketQuote> quotes = await RobinhoodMcpGateway.FetchReplayHistoryAsync(
            Instrument, Start, End, observedAt, (arguments, _) =>
            {
                string interval = Assert.IsType<string>(arguments["interval"]);
                requested.Add(interval);
                return Task.FromResult(interval == "minute"
                    ? Response(interval, Bar(Start), Bar(Start.AddMinutes(1)), Bar(Start.AddMinutes(2)))
                    : Response(interval, Bar(Start.AddSeconds(60))));
            }, CancellationToken.None);

        Assert.Equal(new[] { "15second", "30second", "minute" }, requested);
        MarketQuote quote = Assert.Single(quotes);
        Assert.Equal(Start.ToUniversalTime(), quote.SourceTimestampUtc);
        Assert.Equal(Start.AddMinutes(1).ToUniversalTime(), quote.SourceEndsAtUtc);
    }

    [Fact]
    public async Task ReplayHistory_AllUnavailableStopsBeforeUnsupportedTwoMinuteOrFiveMinute()
    {
        var requested = new List<string>();
        IReadOnlyList<MarketQuote> quotes = await RobinhoodMcpGateway.FetchReplayHistoryAsync(
            Instrument, Start, End, End, (arguments, _) =>
            {
                string interval = Assert.IsType<string>(arguments["interval"]);
                requested.Add(interval);
                return Task.FromResult(Response(interval));
            }, CancellationToken.None);

        Assert.Empty(quotes);
        Assert.Equal(new[] { "15second", "30second", "minute" }, requested);
    }

    [Fact]
    public async Task ReplayHistory_RequestFailureDoesNotAttemptFallback()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RobinhoodMcpGateway.FetchReplayHistoryAsync(
                Instrument, Start, End, End, (_, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("Remote request failed.");
                }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReplayHistory_MismatchedResponseIntervalDoesNotAttemptFallback()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RobinhoodMcpGateway.FetchReplayHistoryAsync(
                Instrument, Start, End, End, (_, _) =>
                {
                    calls++;
                    return Task.FromResult(Response("5minute", Bar(Start)));
                }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReplayHistory_CancellationStopsBeforeNextFallback()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RobinhoodMcpGateway.FetchReplayHistoryAsync(
                Instrument, Start, End, End, (arguments, token) =>
                {
                    calls++;
                    Assert.Equal(cancellation.Token, token);
                    cancellation.Cancel();
                    return Task.FromResult(Response("15second"));
                }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static int IntervalSeconds(string interval) => interval switch
    {
        "15second" => 15,
        "30second" => 30,
        "minute" => 60,
        _ => throw new InvalidOperationException("Unexpected source interval."),
    };

    private static JsonElement Response(string interval, params object[] bars) =>
        JsonSerializer.SerializeToElement(new
        {
            data = new { results = new[] { new { symbol = "NFLX", interval, bars } } },
        });

    private static object Bar(DateTimeOffset at, bool interpolated = false) => new
    {
        begins_at = at.ToUniversalTime().ToString("O"),
        open_price = "79.89",
        high_price = "80.0133",
        low_price = "79.17",
        close_price = "79.24",
        volume = 370827,
        interpolated,
    };
}
