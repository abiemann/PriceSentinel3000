namespace PriceSentinel3000.Core.MarketData;

public sealed record MarketDataRequest(
    Instrument Instrument,
    TimeSpan PollingInterval,
    TimeSpan WarmStartDuration);
