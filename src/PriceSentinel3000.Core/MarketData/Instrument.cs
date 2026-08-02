namespace PriceSentinel3000.Core.MarketData;

public sealed record Instrument
{
    public Instrument(string symbol, AssetClass assetClass = AssetClass.Equity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        Symbol = symbol.Trim().ToUpperInvariant();
        AssetClass = assetClass;
    }

    public string Symbol { get; }
    public AssetClass AssetClass { get; }

    public override string ToString() => Symbol;
}
