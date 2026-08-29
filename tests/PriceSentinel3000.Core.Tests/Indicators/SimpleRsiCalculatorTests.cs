using PriceSentinel3000.Core.Indicators;

namespace PriceSentinel3000.Core.Tests.Indicators;

public sealed class SimpleRsiCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsNullUntilPeriodIsAvailable()
    {
        decimal? result = SimpleRsiCalculator.Calculate(
            Enumerable.Repeat(10m, SimpleRsiCalculator.DefaultPeriod).ToArray());

        Assert.Null(result);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(-1, 0)]
    [InlineData(0, 50)]
    public void Calculate_HandlesOneDirectionAndFlatPrices(
        int change,
        int expected)
    {
        decimal[] prices =
        [
            .. Enumerable.Range(0, SimpleRsiCalculator.DefaultPeriod + 1)
                .Select(index => 100m + index * change),
        ];

        decimal? result = SimpleRsiCalculator.Calculate(prices);

        Assert.Equal((decimal)expected, result);
    }

    [Fact]
    public void Calculate_UsesMostRecentPriceChanges()
    {
        decimal[] prices =
        [
            1_000m,
            .. Enumerable.Range(1, SimpleRsiCalculator.DefaultPeriod + 1)
                .Select(value => (decimal)value),
        ];

        decimal? result = SimpleRsiCalculator.Calculate(prices);

        Assert.Equal(100m, result);
    }

    [Fact]
    public void CalculateSeries_AlignsValuesWithTheirClosingPrices()
    {
        decimal[] prices =
        [
            .. Enumerable.Range(1, SimpleRsiCalculator.DefaultPeriod + 1)
                .Select(value => (decimal)value),
            SimpleRsiCalculator.DefaultPeriod,
        ];

        IReadOnlyList<decimal?> values = SimpleRsiCalculator.CalculateSeries(prices);

        Assert.All(values.Take(SimpleRsiCalculator.DefaultPeriod), Assert.Null);
        Assert.Equal(100m, values[SimpleRsiCalculator.DefaultPeriod]);
        Assert.Equal(
            100m - 100m / SimpleRsiCalculator.DefaultPeriod,
            values[SimpleRsiCalculator.DefaultPeriod + 1]);
    }

    [Fact]
    public void Calculate_RejectsNonPositivePeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimpleRsiCalculator.Calculate([1m, 2m], period: 0));
    }
}
