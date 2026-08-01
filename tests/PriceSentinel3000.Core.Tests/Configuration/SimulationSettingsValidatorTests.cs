using PriceSentinel3000.Core.Configuration;

namespace PriceSentinel3000.Core.Tests.Configuration;

public sealed class SimulationSettingsValidatorTests
{
    [Fact]
    public void DefaultSettings_AreValid()
    {
        IReadOnlyList<string> errors =
            SimulationSettingsValidator.Validate(SimulationSettings.Default);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sofi")]
    [InlineData("BAD SYMBOL")]
    [InlineData("ABCDEFGHIJK")]
    public void InvalidSymbol_IsRejected(string symbol)
    {
        SimulationSettings settings = SimulationSettings.Default with
        {
            Symbol = symbol,
        };

        IReadOnlyList<string> errors = SimulationSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("symbol", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AccountPercentageOverOneHundred_IsRejected()
    {
        SimulationSettings settings = SimulationSettings.Default with
        {
            PositionSizeBasis = AmountBasis.AccountPercentage,
            PositionSizeValue = 101m,
        };

        IReadOnlyList<string> errors = SimulationSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Position size percentage"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void BufferOutsideTunableRange_IsRejected(int minutes)
    {
        SimulationSettings settings = SimulationSettings.Default with
        {
            BufferMinutes = minutes,
        };

        IReadOnlyList<string> errors = SimulationSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Ring buffer"));
    }

    [Fact]
    public void EntryLimitCanBeUnlimited()
    {
        SimulationSettings settings = SimulationSettings.Default with
        {
            UnlimitedEntries = true,
            MaximumEntriesPerDay = 0,
        };

        IReadOnlyList<string> errors = SimulationSettingsValidator.Validate(settings);

        Assert.DoesNotContain(errors, error => error.Contains("Maximum entries"));
    }
}
