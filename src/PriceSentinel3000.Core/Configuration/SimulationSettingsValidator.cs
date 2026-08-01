namespace PriceSentinel3000.Core.Configuration;

public static class SimulationSettingsValidator
{
    public static IReadOnlyList<string> Validate(SimulationSettings settings)
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
        else if (settings.StopLossBasis is StopLossBasis.BuyPercentage &&
                 settings.StopLossValue > 100)
        {
            errors.Add("Stop-loss percentage cannot exceed 100%.");
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
