using System.ComponentModel;
using ModelContextProtocol.Protocol;

namespace PriceSentinel3000.Control;

public sealed partial class AutomationTools
{
    [Description("List validated immutable datasets in the app's configured local market-data library. Works offline without an active Replay or broker login. Returns bounded metadata and diagnostics; no file paths or private watchlist configuration. These archived data are distinct from already-processed simulation observations.")]
    public Task<CallToolResult> LibraryDatasetsAsync(
        [Description("Zero-based dataset offset. Start with zero; continue with nextOffset and catalogHash from the prior response.")] int offset = 0,
        [Description("Maximum datasets, from 1 to 100; a byte limit may shorten the page.")] int limit = 50,
        [Description("Expected catalog hash from the first page. Required for offsets greater than zero; a changed catalog is rejected.")] string? catalogHash = null,
        CancellationToken cancellationToken = default) =>
        SendAsync("library_datasets", new { offset, limit, catalogHash }, cancellationToken);

    [Description("Read a bounded page of original candles from one immutable local dataset hash, without Replay or network access. Returns UTC boundaries, source interval, adjustment provenance, exact decimal strings, and null for unknown volume. Missing or invalid hashes never fall back to another dataset.")]
    public Task<CallToolResult> LibraryCandlesAsync(
        [Description("Exact 64-character lowercase dataset hash returned by library_datasets. Filesystem paths are not accepted.")] string datasetHash,
        [Description("Zero-based candle offset. Continue with nextOffset from the prior page.")] int offset = 0,
        [Description("Maximum candles, from 1 to 100.")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        SendAsync("library_candles", new { datasetHash, offset, limit }, cancellationToken);
}
