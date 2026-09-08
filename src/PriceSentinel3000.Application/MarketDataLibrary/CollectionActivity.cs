namespace PriceSentinel3000.Application.MarketDataLibrary;

/// <summary>The current collection phase, kept in memory independently of durable job state.</summary>
public sealed record CollectionActivity(string Stage, string? Symbol, DateOnly? SessionDate, DateTimeOffset SinceUtc);
