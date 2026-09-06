using System.Text.Json;
using System.Text.Json.Serialization;

namespace PriceSentinel3000.Application.Automation;

public sealed record AutomationRequest(string Command, JsonElement Arguments);

public sealed record AutomationResponse(bool Success, JsonElement? Result, string? ErrorCode, string? Error)
{
    public static AutomationResponse Ok(object result) =>
        new(true, JsonSerializer.SerializeToElement(result, AutomationProtocol.JsonOptions), null, null);

    public static AutomationResponse Fail(string code, string message) => new(false, null, code, message);
}

public static class AutomationProtocol
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
