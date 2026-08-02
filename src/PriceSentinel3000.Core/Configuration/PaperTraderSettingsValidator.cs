namespace PriceSentinel3000.Core.Configuration;

public static class PaperTraderSettingsValidator
{
    public static IReadOnlyList<string> Validate(PaperTraderSettings settings)
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

        if (settings.ReconciliationSeconds is < 15 or > 300)
        {
            errors.Add("Historical reconciliation must be between 15 and 300 seconds.");
        }

        if (settings.ReconciliationOverlapSeconds is < 5 ||
            settings.ReconciliationOverlapSeconds > settings.ReconciliationSeconds)
        {
            errors.Add("Reconciliation overlap must be at least 5 seconds and no longer than the reconciliation interval.");
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

        if (!ReplaySchedule.TryParseLocal(
                settings.ReplayDate,
                settings.ReplayTime,
                out _))
        {
            errors.Add("Replay time must use 24-hour HH:mm format and identify a valid local time.");
        }

        if (settings.ReplayDurationMinutes is < 1 or > 480)
        {
            errors.Add("Replay duration must be between 1 and 480 minutes.");
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
