using System.Reflection;

namespace PriceSentinel3000.Application;

/// <summary>The release label embedded in a build, without its commit metadata.</summary>
public static class BuildVersion
{
    public static string Display(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? label = informational?.Split('+', 2)[0];
        return string.IsNullOrWhiteSpace(label)
            ? assembly.GetName().Version?.ToString(3) ?? "unknown"
            : label;
    }
}
