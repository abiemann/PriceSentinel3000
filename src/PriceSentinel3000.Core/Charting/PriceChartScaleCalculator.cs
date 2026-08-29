namespace PriceSentinel3000.Core.Charting;

public readonly record struct PriceChartRange(decimal Minimum, decimal Maximum)
{
    public decimal Span => Maximum - Minimum;
}

public readonly record struct PriceChartScale(
    decimal Minimum,
    decimal Maximum,
    decimal Step)
{
    public decimal Span => Maximum - Minimum;
}

/// <summary>
/// Pure price-axis calculations shared by chart rendering and manual scale gestures.
/// </summary>
public static class PriceChartScaleCalculator
{
    public static PriceChartScale CreateAutomatic(
        decimal observedMinimum,
        decimal observedMaximum,
        decimal openingPrice)
    {
        decimal minimumSpan = Math.Max(0.01m, Math.Abs(openingPrice) * 0.02m);
        decimal halfMinimumSpan = minimumSpan / 2m;
        decimal observedSpan = observedMaximum - observedMinimum;
        decimal expansionPadding = Math.Max(
            minimumSpan * 0.08m,
            observedSpan * 0.12m);
        decimal rawMinimum = Math.Min(
            openingPrice - halfMinimumSpan,
            observedMinimum - expansionPadding);
        decimal rawMaximum = Math.Max(
            openingPrice + halfMinimumSpan,
            observedMaximum + expansionPadding);

        return CreateRounded(rawMinimum, rawMaximum);
    }

    public static PriceChartRange FitToObserved(
        decimal observedMinimum,
        decimal observedMaximum,
        decimal referencePrice)
    {
        decimal observedSpan = observedMaximum - observedMinimum;
        decimal absoluteReferencePrice = Math.Abs(referencePrice);
        decimal minimumPadding = Math.Max(
            0.0001m,
            absoluteReferencePrice * 0.00005m);
        decimal padding = observedSpan > 0m
            ? Math.Max(observedSpan * 0.08m, minimumPadding)
            : Math.Max(0.005m, absoluteReferencePrice * 0.0005m);
        decimal minimum = Math.Max(0m, observedMinimum - padding);
        decimal maximum = observedMaximum + padding;

        if (maximum <= minimum)
        {
            maximum = minimum + Math.Max(0.0001m, padding * 2m);
        }

        return new(minimum, maximum);
    }

    public static PriceChartRange AdjustBoundary(
        decimal startingMinimum,
        decimal startingMaximum,
        decimal verticalDragFraction,
        bool adjustMaximum)
    {
        decimal startingSpan = startingMaximum - startingMinimum;

        if (startingSpan <= 0m)
        {
            return new(startingMinimum, startingMaximum);
        }

        decimal priceDelta = startingSpan * verticalDragFraction;
        decimal minimumSpan = Math.Max(0.0001m, startingSpan * 0.02m);

        return adjustMaximum
            ? new(
                startingMinimum,
                Math.Max(
                    startingMaximum + priceDelta,
                    startingMinimum + minimumSpan))
            : new(
                Math.Clamp(
                    startingMinimum + priceDelta,
                    0m,
                    startingMaximum - minimumSpan),
                startingMaximum);
    }

    private static PriceChartScale CreateRounded(
        decimal rawMinimum,
        decimal rawMaximum)
    {
        decimal rawSpan = Math.Max(0.0001m, rawMaximum - rawMinimum);
        decimal step = NiceStep(rawSpan / 4m);
        decimal minimum = Math.Floor(rawMinimum / step) * step;
        decimal maximum = Math.Ceiling(rawMaximum / step) * step;

        if (maximum <= minimum)
        {
            maximum = minimum + step;
        }

        return new(minimum, maximum, step);
    }

    private static decimal NiceStep(decimal rawStep)
    {
        double exponent = Math.Floor(Math.Log10((double)rawStep));
        decimal magnitude = (decimal)Math.Pow(10d, exponent);
        decimal fraction = rawStep / magnitude;
        decimal niceFraction = fraction switch
        {
            <= 1m => 1m,
            <= 2m => 2m,
            <= 2.5m => 2.5m,
            <= 5m => 5m,
            _ => 10m,
        };
        return niceFraction * magnitude;
    }
}
