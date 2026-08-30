namespace PriceSentinel3000.Core.MarketData;

public sealed record InstrumentSearchResult(
    string Symbol,
    string Name);

public interface IInstrumentSearchSource
{
    Task<IReadOnlyList<InstrumentSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}
