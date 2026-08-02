using PriceSentinel3000.Core.MarketData;

namespace PriceSentinel3000.Core.Strategy;

public sealed record StrategyPositionContext(
    decimal Quantity,
    decimal AveragePrice,
    DateTimeOffset OpenedAtUtc)
{
    public bool HasPosition => Quantity > 0m;

    public static StrategyPositionContext Flat { get; } = new(0m, 0m, DateTimeOffset.MinValue);
}

/// <summary>
/// A deterministic, reactionary price-action detector. It identifies confirmed
/// turns after they happen; it does not predict a future low or high.
/// </summary>
public interface IPriceActionSignalEngine
{
    StrategyDecision Evaluate(
        IReadOnlyList<MarketQuote> quotes,
        StrategyPositionContext position);
}

public sealed class PriceActionSignalEngine : IPriceActionSignalEngine
{
    public const int RsiPeriod = 14;
    private const decimal TouchTolerancePercent = 0.06m;
    private const decimal MinimumSwingPercent = 0.10m;
    private const decimal MinimumConfirmationPercent = 0.025m;
    private const decimal MinimumProfitableExitPercent = 0.04m;
    private readonly int _blockCount;

    public PriceActionSignalEngine(int blockCount = 7)
    {
        if (blockCount is < 1 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }

        _blockCount = blockCount;
    }

    public StrategyDecision Evaluate(
        IReadOnlyList<MarketQuote> quotes,
        StrategyPositionContext position)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(position);

        MarketQuote[] ordered =
        [
            .. quotes
                .Where(quote => quote.Last > 0m)
                .OrderBy(quote => quote.SourceTimestampUtc),
        ];

        if (ordered.Length == 0)
        {
            return StrategyDecision.Hold(
                DateTimeOffset.UtcNow,
                "WARMING UP",
                "Waiting for the first valid market observation.");
        }

        DateTimeOffset evaluatedAt = ordered[^1].SourceTimestampUtc;

        if (ordered.Length < RsiPeriod + 1)
        {
            return StrategyDecision.Hold(
                evaluatedAt,
                "WARMING UP",
                $"Need {RsiPeriod + 1 - ordered.Length} more observations for RSI({RsiPeriod}).");
        }

        decimal? rsi = CalculateSimpleRsi(ordered.Select(quote => quote.Last).ToArray());
        decimal? priorRsi = CalculateSimpleRsi(
            ordered[..^1].Select(quote => quote.Last).ToArray());
        decimal latest = ordered[^1].Last;
        decimal momentum = PercentChange(PriceNear(ordered, evaluatedAt.AddSeconds(-20)), latest);
        BlockSummary blocks = SummarizeBlocks(MinuteBlockAnalyzer.Analyze(
            ordered,
            _blockCount,
            evaluatedAt));

        return position.HasPosition
            ? EvaluateExit(ordered, position, rsi, priorRsi, momentum, blocks)
            : EvaluateEntry(ordered, rsi, priorRsi, momentum, blocks);
    }

    public static decimal? CalculateSimpleRsi(IReadOnlyList<decimal> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);

        if (prices.Count < RsiPeriod + 1)
        {
            return null;
        }

        decimal gains = 0m;
        decimal losses = 0m;

        for (int index = prices.Count - RsiPeriod; index < prices.Count; index++)
        {
            decimal change = prices[index] - prices[index - 1];

            if (change > 0m)
            {
                gains += change;
            }
            else
            {
                losses -= change;
            }
        }

        decimal averageGain = gains / RsiPeriod;
        decimal averageLoss = losses / RsiPeriod;

        if (averageLoss == 0m)
        {
            return averageGain == 0m ? 50m : 100m;
        }

        decimal relativeStrength = averageGain / averageLoss;
        return Math.Round(100m - 100m / (1m + relativeStrength), 2);
    }

    private static StrategyDecision EvaluateEntry(
        MarketQuote[] quotes,
        decimal? rsi,
        decimal? priorRsi,
        decimal momentum,
        BlockSummary blocks)
    {
        DateTimeOffset now = quotes[^1].SourceTimestampUtc;
        MarketQuote[] window = Recent(quotes, now - TimeSpan.FromMinutes(4));
        decimal low = window.Min(quote => quote.Last);
        int lowIndex = Array.FindLastIndex(window, quote => quote.Last == low);
        decimal highBeforeLow = window[..(lowIndex + 1)].Max(quote => quote.Last);
        decimal decline = PercentChange(highBeforeLow, low) * -1m;
        decimal rebound = PercentChange(low, quotes[^1].Last);
        int lowTouches = CountSeparatedTouches(window, low, isLow: true);
        bool lingering = window.TakeLast(4).Count(
            quote => WithinPercent(quote.Last, low, TouchTolerancePercent)) >= 2;
        bool rsiConfirming = rsi is <= 48m && (priorRsi is null || rsi >= priorRsi);
        bool priceConfirming = momentum >= MinimumConfirmationPercent &&
            rebound >= MinimumConfirmationPercent;
        bool bottomEvidence = lowTouches >= 2 || lingering;
        bool blockSupport = blocks.Down > 0 ||
            blocks.Flat > 0 && decline >= MinimumSwingPercent * 2m;

        decimal confidence = 0m;
        confidence += Math.Min(0.25m, decline / 0.50m * 0.25m);
        confidence += priceConfirming ? 0.30m : 0m;
        confidence += bottomEvidence ? 0.20m : 0m;
        confidence += rsiConfirming ? 0.15m : 0m;
        confidence += blockSupport ? 0.10m : 0m;

        if (decline >= MinimumSwingPercent &&
            priceConfirming &&
            bottomEvidence &&
            rsiConfirming &&
            blockSupport)
        {
            return new(
                now,
                StrategySignalKind.Buy,
                "BOTTOM CONFIRMED",
                Math.Min(1m, confidence),
                [
                    $"Price declined {decline:0.000}% into the recent low.",
                    $"Low zone was tested {lowTouches} time(s); rebound is {rebound:0.000}%.",
                    $"20-second momentum turned positive at {momentum:0.000}%.",
                    $"Simple RSI({RsiPeriod}) is {rsi:0.0} and no longer falling.",
                    $"Minute blocks: {blocks.Down} down, {blocks.Flat} flat, {blocks.Up} up; whole-window move {blocks.OverallMovePercent:0.000}%.",
                ],
                rsi,
                momentum,
                rebound);
        }

        string state = decline >= MinimumSwingPercent ? "BOTTOM WATCH" : "SCANNING";
        return new(
            now,
            StrategySignalKind.Hold,
            state,
            Math.Min(0.99m, confidence),
            [
                $"Decline {decline:0.000}%, rebound {rebound:0.000}%, low tests {lowTouches}.",
                $"Minute blocks {blocks.Down}D/{blocks.Flat}F/{blocks.Up}U; waiting for block, low-zone, momentum, and RSI confirmation together.",
            ],
            rsi,
            momentum,
            rebound);
    }

    private static StrategyDecision EvaluateExit(
        MarketQuote[] quotes,
        StrategyPositionContext position,
        decimal? rsi,
        decimal? priorRsi,
        decimal momentum,
        BlockSummary blocks)
    {
        DateTimeOffset now = quotes[^1].SourceTimestampUtc;
        MarketQuote[] sinceEntry =
        [
            .. quotes.Where(quote => quote.SourceTimestampUtc >= position.OpenedAtUtc),
        ];
        MarketQuote[] window = sinceEntry.Length > 0
            ? sinceEntry
            : Recent(quotes, now - TimeSpan.FromMinutes(4));
        decimal latest = quotes[^1].Last;
        decimal peak = window.Max(quote => quote.Last);
        decimal gain = PercentChange(position.AveragePrice, latest);
        decimal drawdown = PercentChange(peak, latest) * -1m;
        int peakTouches = CountSeparatedTouches(window, peak, isLow: false);
        bool rsiTurning = rsi is >= 62m && priorRsi is not null && rsi < priorRsi;
        bool priceTurning = momentum <= -MinimumConfirmationPercent;
        bool peakEvidence = peakTouches >= 2 || drawdown >= MinimumConfirmationPercent;
        bool heldSeveralMinutes = now - position.OpenedAtUtc >= TimeSpan.FromMinutes(5);
        bool blockSupport = blocks.Up > 0 || gain >= MinimumProfitableExitPercent * 2m;

        decimal confidence = 0m;
        confidence += Math.Min(0.25m, Math.Max(0m, gain) / 0.50m * 0.25m);
        confidence += priceTurning ? 0.30m : 0m;
        confidence += peakEvidence ? 0.20m : 0m;
        confidence += rsiTurning ? 0.15m : 0m;
        confidence += blockSupport ? 0.10m : 0m;

        bool confirmedPeak = gain >= MinimumProfitableExitPercent &&
            priceTurning &&
            (peakEvidence || rsiTurning) &&
            blockSupport;
        bool timedStall = gain > 0m && heldSeveralMinutes && momentum <= 0m;

        if (confirmedPeak || timedStall)
        {
            return new(
                now,
                StrategySignalKind.Sell,
                timedStall && !confirmedPeak ? "PROFIT STALLED" : "PEAK CONFIRMED",
                Math.Min(1m, confidence),
                [
                    $"Open-position gain is {gain:0.000}%; pullback from peak is {drawdown:0.000}%.",
                    $"Peak zone was tested {peakTouches} time(s); momentum is {momentum:0.000}%.",
                    rsi is null
                        ? "RSI is not available."
                        : $"Simple RSI({RsiPeriod}) is {rsi:0.0}.",
                    $"Minute blocks: {blocks.Down} down, {blocks.Flat} flat, {blocks.Up} up; whole-window move {blocks.OverallMovePercent:0.000}%.",
                ],
                rsi,
                momentum,
                drawdown);
        }

        return new(
            now,
            StrategySignalKind.Hold,
            "POSITION WATCH",
            Math.Min(0.99m, confidence),
            [
                $"Gain {gain:0.000}%, peak pullback {drawdown:0.000}%, peak tests {peakTouches}.",
                $"Minute blocks {blocks.Down}D/{blocks.Flat}F/{blocks.Up}U; holding until a profitable peak reversal or timed profit stall is confirmed.",
            ],
            rsi,
            momentum,
            drawdown);
    }

    private static MarketQuote[] Recent(MarketQuote[] quotes, DateTimeOffset cutoff) =>
        [.. quotes.Where(quote => quote.SourceTimestampUtc >= cutoff)];

    private static decimal PriceNear(MarketQuote[] quotes, DateTimeOffset target)
    {
        MarketQuote? match = quotes.LastOrDefault(quote => quote.SourceTimestampUtc <= target);
        return match?.Last ?? quotes[Math.Max(0, quotes.Length - 5)].Last;
    }

    private static int CountSeparatedTouches(
        IReadOnlyList<MarketQuote> quotes,
        decimal reference,
        bool isLow)
    {
        DateTimeOffset? prior = null;
        int touches = 0;

        foreach (MarketQuote quote in quotes)
        {
            bool inZone = isLow
                ? quote.Last <= reference * (1m + TouchTolerancePercent / 100m)
                : quote.Last >= reference * (1m - TouchTolerancePercent / 100m);

            if (!inZone || prior is not null && quote.SourceTimestampUtc - prior < TimeSpan.FromSeconds(15))
            {
                continue;
            }

            touches++;
            prior = quote.SourceTimestampUtc;
        }

        return touches;
    }

    private static bool WithinPercent(decimal value, decimal reference, decimal tolerancePercent) =>
        reference > 0m && Math.Abs(value - reference) / reference * 100m <= tolerancePercent;

    private static decimal PercentChange(decimal from, decimal to) =>
        from == 0m ? 0m : (to - from) / from * 100m;

    private static BlockSummary SummarizeBlocks(IReadOnlyList<MinuteBlock> blocks)
    {
        MinuteBlock[] populated = [.. blocks.Where(block => block.QuoteCount > 0)];
        decimal? firstOpen = populated.FirstOrDefault()?.Open;
        decimal? lastClose = populated.LastOrDefault()?.Close;
        decimal overall = firstOpen.HasValue && lastClose.HasValue
            ? PercentChange(firstOpen.GetValueOrDefault(), lastClose.GetValueOrDefault())
            : 0m;
        return new(
            populated.Count(block => block.Direction is PriceDirection.Down),
            populated.Count(block => block.Direction is PriceDirection.Flat),
            populated.Count(block => block.Direction is PriceDirection.Up),
            overall);
    }

    private sealed record BlockSummary(
        int Down,
        int Flat,
        int Up,
        decimal OverallMovePercent);
}
