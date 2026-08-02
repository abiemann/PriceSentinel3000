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

    [Fact]
    public void PurchasePriceDeclineOverOneHundredPercent_IsRejected()
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            StopLossBasis = StopLossBasis.PurchasePriceDeclinePercentage,
            StopLossValue = 101m,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Purchase-price decline"));
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

    [Fact]
    public void UserSpecifiedQuantityMustBePositive()
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            QuantityLimitMode = QuantityLimitMode.NoMoreThan,
            MaximumQuantity = 0m,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("trading quantity"));
    }

    [Theory]
    [InlineData("09:30", "09:30")]
    [InlineData("09:30", "17:31")]
    [InlineData("09:30", "09:29")]
    public void ReplayRangeOutsideMaximumDuration_IsRejected(
        string startTime,
        string endTime)
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            ReplayTime = startTime,
            ReplayEndTime = endTime,
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Replay range"));
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
    public void InvalidReplayEndTime_IsRejected()
    {
        PaperTraderSettings settings = PaperTraderSettings.Default with
        {
            ReplayEndTime = "25:00",
        };

        IReadOnlyList<string> errors = PaperTraderSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("Replay end"));
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

    [Fact]
    public void ReplaySchedule_EndBeforeStartMeansTheFollowingDay()
    {
        bool parsed = ReplaySchedule.TryParseLocalRange(
            "2026-07-31",
            "23:30",
            "00:30",
            out DateTimeOffset replayStart,
            out DateTimeOffset replayEnd);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromHours(1), replayEnd - replayStart);
        Assert.Equal(replayStart.Date.AddDays(1), replayEnd.Date);
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
