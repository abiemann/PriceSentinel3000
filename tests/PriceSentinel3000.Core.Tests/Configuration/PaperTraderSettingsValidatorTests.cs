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
    [InlineData(481)]
    public void ReplayDurationOutsideRange_IsRejected(int minutes)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            ReplayDurationMinutes = minutes,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Replay duration"));
    }

    [Theory]
    [InlineData("08/01/2026", "09:30")]
    [InlineData("2026-08-01", "25:00")]
    public void InvalidReplayDateOrTime_IsRejected(string date, string time)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            ReplayDate = date,
            ReplayTime = time,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Replay", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplaySchedule_ParsesEnteredLocalDateAndTime()
    {
        bool parsed = ReplaySchedule.TryParseLocal(
            "2026-07-31",
            "09:30",
            out DateTimeOffset replayStart);

        Assert.True(parsed);
        Assert.Equal(2026, replayStart.Year);
        Assert.Equal(7, replayStart.Month);
        Assert.Equal(31, replayStart.Day);
        Assert.Equal(9, replayStart.Hour);
        Assert.Equal(30, replayStart.Minute);
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
