namespace PriceSentinel3000.App.ViewModels;

public enum ExistingLivePositionChoice
{
    Cancel,
    SellNow,
    MonitorForProfit,
}

public sealed record ExistingLivePositionPrompt(
    string Symbol,
    decimal Quantity,
    decimal SharesAvailableForSale,
    decimal AverageBuyPrice,
    decimal EstimatedSellPrice,
    string EstimatedPriceSource,
    bool CanSellNow,
    string? SellNowBlockReason,
    bool CanMonitorForProfit,
    string? MonitorBlockReason);
