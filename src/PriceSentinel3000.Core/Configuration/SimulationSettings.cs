namespace PriceSentinel3000.Core.Configuration;

public sealed record SimulationSettings
{
    public string Symbol { get; init; } = "SOFI";
    public decimal StartingBalance { get; init; } = 10_000m;
    public AmountBasis PositionSizeBasis { get; init; } = AmountBasis.AccountPercentage;
    public decimal PositionSizeValue { get; init; } = 5m;
    public bool UnlimitedEntries { get; init; }
    public int MaximumEntriesPerDay { get; init; } = 10;
    public AmountBasis MaximumDailyLossBasis { get; init; } = AmountBasis.AccountPercentage;
    public decimal MaximumDailyLossValue { get; init; } = 2m;
    public StopLossBasis StopLossBasis { get; init; } = StopLossBasis.BuyPercentage;
    public decimal StopLossValue { get; init; } = 1m;
    public int BufferMinutes { get; init; } = 7;
    public int QuotePollingSeconds { get; init; } = 5;
    public int ReconciliationSeconds { get; init; } = 45;
    public int ReconciliationOverlapSeconds { get; init; } = 15;

    public static SimulationSettings Default { get; } = new();
}
