using System.Text.Json;

namespace PriceSentinel3000.Application.MarketDataLibrary;

public sealed record DownloadListMember(
    string Symbol,
    string? CompanyName = null,
    bool IsIncluded = true,
    string? ProviderInstrumentId = null);

public sealed record DownloadList(
    Guid Id,
    string Name,
    bool IsEnabled,
    IReadOnlyList<DownloadListMember> Members,
    string? SourceListId = null,
    string? SourceListName = null);

public sealed record CollectionSettings
{
    public const string AllAvailableSessionBounds = "24_5";
    public string LibraryRootPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PriceSentinel3000", "MarketData");
    public IReadOnlyList<DownloadList> Lists { get; init; } = [];
    public bool AutomaticDownloadsEnabled { get; init; }
    public TimeOnly DailyDownloadTime { get; init; } = new(13, 15);
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public DateTimeOffset? AutomaticEnabledAtUtc { get; init; }
    public string SessionBounds { get; init; } = "regular";
    public int ProviderFinalizationDelayMinutes { get; init; } = 15;
    public int CatchUpCalendarDays { get; init; } = 7;

    public CollectionSettings Validate()
    {
        if (string.IsNullOrWhiteSpace(LibraryRootPath) || !Path.IsPathFullyQualified(LibraryRootPath))
            throw new ArgumentException("Choose an absolute data-library folder.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        if (DailyDownloadTime.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("The daily download time must use hours and minutes.");
        if (SessionBounds is not ("regular" or "extended" or "24_5"))
            throw new ArgumentException("Unknown session coverage.");
        if (ProviderFinalizationDelayMinutes is < 0 or > 1440 || CatchUpCalendarDays is < 1 or > 30)
            throw new ArgumentException("Finalization delay must be 0–1440 minutes and catch-up 1–30 calendar days.");
        if (Lists is null || Lists.Count > 100 || Lists.Select(l => l.Id).Distinct().Count() != Lists.Count)
            throw new ArgumentException("Download lists must have unique identifiers; at most 100 lists are supported.");
        var normalized = new List<DownloadList>();
        foreach (DownloadList list in Lists)
        {
            if (list.Id == Guid.Empty || string.IsNullOrWhiteSpace(list.Name) || list.Name.Trim().Length > 100 ||
                list.Members is null || list.Members.Count > 1000)
                throw new ArgumentException("Each list needs a name, an identifier, and at most 1,000 equities.");
            DownloadListMember[] members = list.Members.Select(m => m with { Symbol = NormalizeSymbol(m.Symbol) }).ToArray();
            if (members.Select(m => m.Symbol).Distinct(StringComparer.Ordinal).Count() != members.Length)
                throw new ArgumentException($"List '{list.Name}' contains duplicate symbols.");
            normalized.Add(list with { Name = list.Name.Trim(), Members = members });
        }
        return this with { LibraryRootPath = Path.GetFullPath(LibraryRootPath), Lists = normalized.ToArray() };
    }

    public static string NormalizeSymbol(string symbol)
    {
        string value = symbol?.Trim().ToUpperInvariant() ?? "";
        if (value.Length is < 1 or > 16 || !char.IsAsciiLetter(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw new ArgumentException($"Invalid equity symbol '{value}'.");
        return value;
    }
}

/// <summary>Portable list copies intentionally contain neither provider IDs nor private source-list provenance.</summary>
public static class DownloadListTransfer
{
    private sealed record PortableMember(string Symbol, bool IsIncluded);
    private sealed record PortableList(string Name, bool IsEnabled, IReadOnlyList<PortableMember> Members);
    private sealed record PortableDocument(int SchemaVersion, IReadOnlyList<PortableList> Lists);

    public static string Export(IReadOnlyList<DownloadList> lists) => JsonSerializer.Serialize(
        new PortableDocument(1, lists.Select(l => new PortableList(l.Name, l.IsEnabled,
            l.Members.Select(m => new PortableMember(CollectionSettings.NormalizeSymbol(m.Symbol), m.IsIncluded)).ToArray())).ToArray()),
        new JsonSerializerOptions { WriteIndented = true });

    public static IReadOnlyList<DownloadList> Import(string json)
    {
        if (json.Length > 2_000_000) throw new ArgumentException("List import exceeds the 2 MB limit.");
        PortableDocument document = JsonSerializer.Deserialize<PortableDocument>(json)
            ?? throw new ArgumentException("The list file is empty.");
        if (document.SchemaVersion != 1 || document.Lists is null)
            throw new ArgumentException("Unsupported download-list file version.");
        DownloadList[] lists = document.Lists.Select(l => new DownloadList(Guid.NewGuid(), l.Name, l.IsEnabled,
            l.Members.Select(m => new DownloadListMember(m.Symbol, IsIncluded: m.IsIncluded)).ToArray())).ToArray();
        return (new CollectionSettings { Lists = lists }).Validate().Lists;
    }
}
