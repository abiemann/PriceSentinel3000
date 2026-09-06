using System.Text.Json.Serialization;

namespace PriceSentinel3000.Control;

public enum AutomationCandleKind
{
    [JsonStringEnumMemberName("strategy")]
    Strategy,
    [JsonStringEnumMemberName("source")]
    Source,
}
