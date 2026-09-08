using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PriceSentinel3000.Application.Automation;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<AutomationResponse> ReadAutomationLibraryDatasetsAsync(LibraryDatasetArguments arguments)
    {
        ValidateLibraryPage(arguments.Offset, arguments.Limit);
        if (arguments.CatalogHash is not null) ValidateLibraryHash(arguments.CatalogHash);
        if (arguments.Offset > 0 && arguments.CatalogHash is null)
            throw new ArgumentException("Continue dataset pages with the catalogHash returned by the first page.");
        if (DataRetention is null) return AutomationResponse.Fail("library_unavailable", "The local data library is not configured.");
        IMarketDataLibrary library = DataRetention.CreateLibrary();
        try
        {
            MarketDataLibraryScan scan = await Task.Run(library.Scan);
            object[] datasets = scan.Datasets.Select(LibraryDatasetMetadata).ToArray();
            var diagnostics = scan.Diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal)
                .Select(item => new { item.Code, message = LibraryDiagnosticMessage(item.Code) }).ToArray();
            string catalogHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { datasets, diagnostics }, AutomationProtocol.JsonOptions)));
            if (arguments.CatalogHash is { } expected && expected != catalogHash)
                return AutomationResponse.Fail("library_catalog_changed", "The validated library catalog changed. Restart pagination at offset zero.");
            if (arguments.Offset > datasets.Length) throw new ArgumentException("The offset exceeds the dataset count.");
            int count = Math.Min(arguments.Limit, datasets.Length - arguments.Offset);
            AutomationResponse response;
            do
            {
                response = AutomationResponse.Ok(new
                {
                    catalogHash, offset = arguments.Offset, totalCount = datasets.Length,
                    nextOffset = arguments.Offset + count,
                    hasMore = arguments.Offset + count < datasets.Length,
                    datasets = datasets.Skip(arguments.Offset).Take(count).ToArray(),
                    diagnostics = diagnostics.Take(20).ToArray(), diagnosticCount = diagnostics.Length,
                    diagnosticsTruncated = diagnostics.Length > 20,
                });
                if (JsonSerializer.SerializeToUtf8Bytes(response, AutomationProtocol.JsonOptions).Length <= ResearchPageByteLimit) return response;
                count /= 2;
            } while (count > 0);
            return AutomationResponse.Fail("library_page_too_large", "A library metadata page exceeds the response byte limit.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return AutomationResponse.Fail("library_scan_failed", "The configured library could not be scanned. Inspect its diagnostics in Retain Hi-Res Data.");
        }
    }

    private async Task<AutomationResponse> ReadAutomationLibraryCandlesAsync(LibraryCandleArguments arguments)
    {
        ValidateLibraryHash(arguments.DatasetHash);
        ValidateLibraryPage(arguments.Offset, arguments.Limit);
        if (DataRetention is null) return AutomationResponse.Fail("library_unavailable", "The local data library is not configured.");
        IMarketDataLibrary library = DataRetention.CreateLibrary();
        try
        {
            HistoricalDataset dataset = await Task.Run(() => library.Read(arguments.DatasetHash));
            if (arguments.Offset > dataset.Candles.Count) throw new ArgumentException("The offset exceeds the candle count.");
            var candles = dataset.Candles.Skip(arguments.Offset).Take(arguments.Limit).Select(item => new
            {
                item.StartsAtUtc, item.EndsAtUtc, item.AvailableAtUtc,
                open = ExactLibraryDecimal(item.Open), high = ExactLibraryDecimal(item.High),
                low = ExactLibraryDecimal(item.Low), close = ExactLibraryDecimal(item.Close),
                volume = item.Volume is { } volume ? ExactLibraryDecimal(volume) : null,
            }).ToArray();
            return AutomationResponse.Ok(new
            {
                dataset.DatasetHash, dataset.SchemaVersion, dataset.GroupingTimeZone,
                dataset.Provider, dataset.InstrumentId, dataset.Symbol, dataset.TradingDate,
                dataset.SourceIntervalSeconds, dataset.AdjustmentPolicy, dataset.AdjustmentBasis,
                dataset.SessionBounds, dataset.FetchedAtUtc, coverage = LibraryCoverageSummary(dataset.Coverage),
                offset = arguments.Offset, nextOffset = arguments.Offset + candles.Length,
                totalCount = dataset.Candles.Count, hasMore = arguments.Offset + candles.Length < dataset.Candles.Count,
                decimalEncoding = "invariant strings; null volume means unknown", candles,
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return AutomationResponse.Fail("library_dataset_unavailable", "The selected dataset is missing or failed validation. No other revision was substituted.");
        }
    }

    private static object LibraryDatasetMetadata(HistoricalDatasetInfo item) => new
    {
        item.DatasetHash, item.Provider, item.InstrumentId, item.Symbol, item.TradingDate,
        item.SourceIntervalSeconds, item.AdjustmentPolicy, item.AdjustmentBasis, item.SessionBounds,
        item.FetchedAtUtc, coverage = LibraryCoverageSummary(item.Coverage),
    };
    private static object LibraryCoverageSummary(HistoricalCoverage coverage) => new
    {
        coverage.RequestedFromUtc, coverage.RequestedThroughUtc, coverage.CoveredFromUtc, coverage.CoveredThroughUtc,
        coverage.ExpectedCandleCount, coverage.ActualCandleCount, coverage.Complete, coverage.HasCompleteVolume,
        gapCount = coverage.Gaps.Count,
    };
    private static string LibraryDiagnosticMessage(string code) => code switch
    {
        "conflicting_revisions" => "Competing daily revisions require explicit hash selection or a revision policy.",
        "interrupted_write" => "An unfinished temporary file was ignored.",
        "scan_limit" => "The library exceeds the bounded scan limit.",
        "scan_failed" => "The library scan could not be completed.",
        _ => "A candle file failed schema, coverage, or integrity validation.",
    };
    private static string ExactLibraryDecimal(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    private static void ValidateLibraryHash(string? hash)
    {
        if (hash is null || !Regex.IsMatch(hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Specify a 64-character lowercase hexadecimal dataset or catalog hash.");
    }
    private static void ValidateLibraryPage(int offset, int limit)
    {
        if (offset is < 0 or > 10_000 || limit is < 1 or > 100)
            throw new ArgumentException("Library pages require offset 0–10000 and limit 1–100.");
    }
    private sealed record LibraryDatasetArguments(int Offset = 0, int Limit = 50, string? CatalogHash = null);
    private sealed record LibraryCandleArguments(string DatasetHash, int Offset = 0, int Limit = 50);
}
