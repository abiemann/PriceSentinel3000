namespace PriceSentinel3000.Core.MarketData;

public static class MinuteBlockAnalyzer
{
    private const decimal FlatTolerancePercent = 0.02m;

    public static IReadOnlyList<MinuteBlock> Analyze(
        IReadOnlyList<MarketQuote> quotes,
        int blockCount,
        DateTimeOffset? windowEndUtc = null)
    {
        ArgumentNullException.ThrowIfNull(quotes);

        if (blockCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }

        DateTimeOffset end = windowEndUtc ??
            (quotes.Count == 0 ? DateTimeOffset.UtcNow : quotes.Max(quote => quote.SourceTimestampUtc));
        var blocks = new List<MinuteBlock>(blockCount);

        for (int index = 0; index < blockCount; index++)
        {
            DateTimeOffset startsAt = end - TimeSpan.FromMinutes(blockCount - index);
            DateTimeOffset endsAt = startsAt + TimeSpan.FromMinutes(1);
            bool isLast = index == blockCount - 1;
            MarketQuote[] members =
            [
                .. quotes.Where(quote =>
                    quote.SourceTimestampUtc >= startsAt &&
                    (quote.SourceTimestampUtc < endsAt ||
                     isLast && quote.SourceTimestampUtc == endsAt))
                    .OrderBy(quote => quote.SourceTimestampUtc),
            ];

            if (members.Length == 0)
            {
                blocks.Add(new(
                    index + 1,
                    startsAt,
                    endsAt,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    PriceDirection.Empty));
                continue;
            }

            decimal open = members[0].Last;
            decimal close = members[^1].Last;
            decimal changePercent = open == 0m ? 0m : (close - open) / open * 100m;
            PriceDirection direction = changePercent switch
            {
                > FlatTolerancePercent => PriceDirection.Up,
                < -FlatTolerancePercent => PriceDirection.Down,
                _ => PriceDirection.Flat,
            };

            blocks.Add(new(
                index + 1,
                startsAt,
                endsAt,
                members.Length,
                open,
                members.Max(quote => quote.Last),
                members.Min(quote => quote.Last),
                close,
                changePercent,
                direction));
        }

        return blocks;
    }
}
