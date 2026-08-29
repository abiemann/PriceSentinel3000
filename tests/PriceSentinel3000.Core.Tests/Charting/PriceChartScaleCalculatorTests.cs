using PriceSentinel3000.Core.Charting;

namespace PriceSentinel3000.Core.Tests.Charting;

public sealed class PriceChartScaleCalculatorTests
{
    [Fact]
    public void CreateAutomatic_PadsObservedPricesAndRoundsTheAxis()
    {
        PriceChartScale scale = PriceChartScaleCalculator.CreateAutomatic(
            observedMinimum: 99m,
            observedMaximum: 101m,
            openingPrice: 100m);

        Assert.Equal(98m, scale.Minimum);
        Assert.Equal(102m, scale.Maximum);
        Assert.Equal(1m, scale.Step);
        Assert.Equal(4m, scale.Span);
    }

    [Fact]
    public void FitToObserved_LeavesEightPercentMarginAroundCandles()
    {
        PriceChartRange range = PriceChartScaleCalculator.FitToObserved(
            observedMinimum: 92.25m,
            observedMaximum: 92.48m,
            referencePrice: 92.48m);

        Assert.Equal(92.2316m, range.Minimum);
        Assert.Equal(92.4984m, range.Maximum);
    }

    [Fact]
    public void FitToObserved_ProvidesMarginForFlatCandles()
    {
        PriceChartRange range = PriceChartScaleCalculator.FitToObserved(
            observedMinimum: 100m,
            observedMaximum: 100m,
            referencePrice: 100m);

        Assert.Equal(99.95m, range.Minimum);
        Assert.Equal(100.05m, range.Maximum);
    }

    [Fact]
    public void AdjustBoundary_ChangesOnlyTheSelectedBoundary()
    {
        PriceChartRange top = PriceChartScaleCalculator.AdjustBoundary(
            100m,
            110m,
            verticalDragFraction: 0.1m,
            adjustMaximum: true);
        PriceChartRange bottom = PriceChartScaleCalculator.AdjustBoundary(
            100m,
            110m,
            verticalDragFraction: 0.1m,
            adjustMaximum: false);

        Assert.Equal(new PriceChartRange(100m, 111m), top);
        Assert.Equal(new PriceChartRange(101m, 110m), bottom);
    }

    [Fact]
    public void AdjustBoundary_EnforcesPriceFloorAndMinimumSpan()
    {
        PriceChartRange bottom = PriceChartScaleCalculator.AdjustBoundary(
            1m,
            11m,
            verticalDragFraction: -1m,
            adjustMaximum: false);
        PriceChartRange top = PriceChartScaleCalculator.AdjustBoundary(
            100m,
            110m,
            verticalDragFraction: -2m,
            adjustMaximum: true);

        Assert.Equal(new PriceChartRange(0m, 11m), bottom);
        Assert.Equal(new PriceChartRange(100m, 100.2m), top);
    }
}
