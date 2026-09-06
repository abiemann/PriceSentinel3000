using PriceSentinel3000.Core.Configuration;

namespace PriceSentinel3000.Control;

public enum AutomationMode { Replay, PaperTrader }

// Nullable properties preserve the current visible settings when omitted.
public sealed record AutomationSettingsPatch
{
    public string? Symbol { get; init; }
    public decimal? StartingBalance { get; init; }
    public bool? TradesSettleImmediately { get; init; }
    public AmountBasis? PositionSizeBasis { get; init; }
    public decimal? PositionSizeValue { get; init; }
    public QuantityLimitMode? QuantityLimitMode { get; init; }
    public decimal? MaximumQuantity { get; init; }
    public bool? UnlimitedEntries { get; init; }
    public int? MaximumEntriesPerDay { get; init; }
    public AmountBasis? MaximumDailyLossBasis { get; init; }
    public decimal? MaximumDailyLossValue { get; init; }
    public StopLossBasis? StopLossBasis { get; init; }
    public decimal? StopLossValue { get; init; }
    public int? BufferMinutes { get; init; }
    public int? QuotePollingSeconds { get; init; }
    public string? StrategyId { get; init; }
    public int? ScriptBarIntervalSeconds { get; init; }
    public int? ChartCandleIntervalSeconds { get; init; }
    public int? ReconciliationSeconds { get; init; }
    public int? ReconciliationLookbackSeconds { get; init; }
    public int? ReconciliationCompletionDelaySeconds { get; init; }
    public string? ReplayDate { get; init; }
    public string? ReplayTime { get; init; }
    public string? ReplayEndTime { get; init; }
    public decimal? ReplaySpeed { get; init; }
}
