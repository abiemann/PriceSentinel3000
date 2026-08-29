namespace PriceSentinel3000.Core.Configuration;

public static class TradingSessionSettingsValidator
{
    public static IReadOnlyList<string> Validate(TradingSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();
        string symbol = settings.Symbol.Trim();

        if (!IsValidSymbol(symbol))
        {
            errors.Add("Enter a valid equity symbol using letters, numbers, '.' or '-'.");
        }

        if (settings.StartingBalance <= 0)
        {
            errors.Add("Starting balance must be greater than zero.");
        }

        ValidateAmount(
            settings.PositionSizeBasis,
            settings.PositionSizeValue,
            settings.StartingBalance,
            "Position size",
            errors);

        if (settings.QuantityLimitMode is QuantityLimitMode.NoMoreThan &&
            settings.MaximumQuantity <= 0m)
        {
            errors.Add("Maximum trading quantity must be greater than zero.");
        }

        if (!settings.UnlimitedEntries && settings.MaximumEntriesPerDay < 1)
        {
            errors.Add("Maximum entries per day must be at least one.");
        }

        ValidateAmount(
            settings.MaximumDailyLossBasis,
            settings.MaximumDailyLossValue,
            settings.StartingBalance,
            "Maximum daily loss",
            errors);

        if (settings.StopLossValue <= 0)
        {
            errors.Add("Stop loss must be greater than zero.");
        }
        else if (settings.StopLossBasis is
                     StopLossBasis.PurchasePriceDeclinePercentage &&
                 settings.StopLossValue > 100m)
        {
            errors.Add("Purchase-price decline cannot exceed 100%.");
        }

        if (settings.BufferMinutes is < 5 or > 15)
        {
            errors.Add("Ring buffer must be between 5 and 15 minutes.");
        }

        if (settings.QuotePollingSeconds is < 1 or > 60)
        {
            errors.Add("Quote polling must be between 1 and 60 seconds.");
        }

        if (settings.ChartCandleIntervalSeconds is not (15 or 30 or 60 or 120))
        {
            errors.Add("Chart candle interval must be 15, 30, 60, or 120 seconds.");
        }

        if (settings.ReconciliationSeconds is < 15 or > 300)
        {
            errors.Add("Historical reconciliation must be between 15 and 300 seconds.");
        }

        if (settings.ReconciliationLookbackSeconds is < 60 or > 3600)
        {
            errors.Add("Historical reconciliation lookback must be between 60 and 3600 seconds.");
        }

        if (settings.ReconciliationCompletionDelaySeconds is < 0 or > 300)
        {
            errors.Add("Historical reconciliation completion delay must be between 0 and 300 seconds.");
        }

        if (!DateOnly.TryParseExact(
                settings.ReplayDate.Trim(),
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _))
        {
            errors.Add("Replay date must use yyyy-MM-dd format.");
        }

        bool hasValidReplayStart = ReplaySchedule.TryParseLocal(
                settings.ReplayDate,
                settings.ReplayTime,
                out _);
        if (!hasValidReplayStart)
        {
            errors.Add("Replay start must use 24-hour HH:mm format and identify a valid local time.");
        }

        bool hasValidReplayEnd = ReplaySchedule.TryParseLocal(
            settings.ReplayDate,
            settings.ReplayEndTime,
            out _);
        if (!hasValidReplayEnd)
        {
            errors.Add("Replay end must use 24-hour HH:mm format and identify a valid local time.");
        }

        if (hasValidReplayStart && hasValidReplayEnd)
        {
            bool hasValidReplayRange = ReplaySchedule.TryParseLocalRange(
                settings.ReplayDate,
                settings.ReplayTime,
                settings.ReplayEndTime,
                out DateTimeOffset replayStart,
                out DateTimeOffset replayEnd);
            TimeSpan replayDuration = replayEnd - replayStart;

            if (!hasValidReplayRange ||
                replayDuration < TimeSpan.FromMinutes(1) ||
                replayDuration > TimeSpan.FromHours(24))
            {
                errors.Add("Replay range must be between 1 minute and 24 hours.");
            }
        }

        if (settings.ReplaySpeed is < 1 or > 100)
        {
            errors.Add("Replay speed must be between 1x and 100x.");
        }

        return errors;
    }

    private static bool IsValidSymbol(string symbol)
    {
        if (symbol.Length is < 1 or > 10)
        {
            return false;
        }

        return symbol.All(character =>
            char.IsAsciiLetterUpper(character) ||
            char.IsAsciiDigit(character) ||
            character is '.' or '-');
    }

    private static void ValidateAmount(
        AmountBasis basis,
        decimal value,
        decimal accountBalance,
        string label,
        ICollection<string> errors)
    {
        if (value <= 0)
        {
            errors.Add($"{label} must be greater than zero.");
            return;
        }

        if (basis is AmountBasis.AccountPercentage && value > 100)
        {
            errors.Add($"{label} percentage cannot exceed 100%.");
        }
        else if (basis is AmountBasis.FixedAmount &&
                 accountBalance > 0 &&
                 value > accountBalance)
        {
            errors.Add($"{label} cannot exceed the starting balance.");
        }
    }
}
