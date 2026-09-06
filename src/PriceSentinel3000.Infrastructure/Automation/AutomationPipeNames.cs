using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PriceSentinel3000.Infrastructure.Automation;

public static class AutomationPipeNames
{
    // The SID keeps the endpoint stable across restarts, without sharing it across users.
    public static string Default { get; } = CreateDefault();

    private static string CreateDefault()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return "PriceSentinel3000.Automation.v1." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.User!.Value)))[..24];
    }
}
