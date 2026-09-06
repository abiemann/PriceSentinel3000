using PriceSentinel3000.Core.Scripting;

namespace PriceSentinel3000.Application.Strategies;

public sealed record StrategyDescriptor(
    string Id,
    string Name,
    string? FileName,
    string? SourceSha256,
    string RuntimeVersion)
{
    public const string BuiltInId = "builtin";
    public static StrategyDescriptor BuiltIn { get; } =
        new(BuiltInId, "Built-In", null, null, "price-action-v1");

    public bool IsBuiltIn => Id == BuiltInId;
}

public sealed record StrategyCatalogDiagnostic(
    string FileName,
    string Message,
    int? Line = null,
    bool IsError = true);

public sealed record StrategyCatalogSnapshot(
    IReadOnlyList<StrategyDescriptor> Strategies,
    IReadOnlyList<StrategyCatalogDiagnostic> Diagnostics);

public sealed record PinnedStrategy(
    StrategyDescriptor Descriptor,
    string? Source,
    CompiledThinkScript? Program);

public interface IStrategyCatalog
{
    string DirectoryPath { get; }

    StrategyCatalogSnapshot Load();

    PinnedStrategy GetPinned(string id);
}
