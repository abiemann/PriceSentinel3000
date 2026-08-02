namespace PriceSentinel3000.App.ViewModels;

public sealed record PricePointViewModel(DateTimeOffset TimestampUtc, decimal Price);
