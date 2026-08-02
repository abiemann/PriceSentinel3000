namespace PriceSentinel3000.Core.Configuration;

public sealed record PaperTraderSettings
{
    public string Symbol { get; init; } = "SOFI";
    public decimal StartingBalance { get; init; } = 10_000m;
    public AmountBasis PositionSizeBasis { get; init; } = AmountBasis.AccountPercentage;
    public decimal PositionSizeValue { get; init; } = 5m;
    public QuantityLimitMode QuantityLimitMode { get; init; } = QuantityLimitMode.AsManyAsPossible;
    public decimal MaximumQuantity { get; init; } = 100m;
    public bool UnlimitedEntries { get; init; }
    public int MaximumEntriesPerDay { get; init; } = 10;
    public AmountBasis MaximumDailyLossBasis { get; init; } = AmountBasis.AccountPercentage;
    public decimal MaximumDailyLossValue { get; init; } = 2m;
    public StopLossBasis StopLossBasis { get; init; } = StopLossBasis.BuyPriceAmount;
    public decimal StopLossValue { get; init; } = 1m;
    public int BufferMinutes { get; init; } = 7;
    public int QuotePollingSeconds { get; init; } = 5;
    public int ReconciliationSeconds { get; init; } = 45;
    public int ReconciliationOverlapSeconds { get; init; } = 15;
    public string ReplayDate { get; init; } = GetDefaultReplayDate();
    public string ReplayTime { get; init; } = "09:30";
    public int ReplayDurationMinutes { get; init; } = 90;
    public decimal ReplaySpeed { get; init; } = 10m;

    public static PaperTraderSettings Default { get; } = new();

    private static string GetDefaultReplayDate()
    {
        DateOnly date = DateOnly.FromDateTime(DateTime.Today);

        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            date = date.AddDays(-1);
        }

        return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
