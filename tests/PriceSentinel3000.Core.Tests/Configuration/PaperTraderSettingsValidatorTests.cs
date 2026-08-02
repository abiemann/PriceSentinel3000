using PriceSentinel3000.Core.Configuration;

namespace PriceSentinel3000.Core.Tests.Configuration;

public sealed class PaperTraderSettingsValidatorTests
{
    [Fact]
    public void DefaultSettings_AreValid()
    {
        IReadOnlyList<string> errors =
            PaperTraderSettingsValidator.Validate(PaperTraderSettings.Default);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sofi")]
    [InlineData("BAD SYMBOL")]
    [InlineData("ABCDEFGHIJK")]
    public void InvalidSymbol_IsRejected(string symbol)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            Symbol = symbol,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("symbol", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AccountPercentageOverOneHundred_IsRejected()
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            PositionSizeBasis = AmountBasis.AccountPercentage,
            PositionSizeValue = 101m,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Position size percentage"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void BufferOutsideTunableRange_IsRejected(int minutes)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            BufferMinutes = minutes,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Ring buffer"));
    }

    [Fact]
    public void EntryLimitCanBeUnlimited()
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            UnlimitedEntries = true,
            MaximumEntriesPerDay = 0,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.DoesNotContain(errors, error => error.Contains("Maximum entries"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void ReplayLookbackOutsideRange_IsRejected(int days)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            ReplayLookbackDays = days,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Replay lookback"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ReplaySpeedOutsideRange_IsRejected(int speed)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            ReplaySpeed = speed,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Replay speed"));
    }
}
