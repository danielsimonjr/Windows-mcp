namespace WindowsMcp.Services;

/// <summary>
/// Blocks registry writes that install persistence or hijack execution
/// (IFEO, Winlogon, AppInit, Services, Policies). Run keys remain allowed
/// behind the existing <c>confirm:true</c> gate — they are a common, intentional
/// startup-management path.
/// </summary>
internal static class RegistryPolicy
{
    private static readonly string[] SensitiveFragments =
    [
        @"\image file execution options",
        @"\winlogon",
        @"\appinit_dlls",
        @"\currentcontrolset\services\",
        @"\policies\",
        @"\windows\currentversion\policies",
    ];

    public static void ThrowIfSensitiveWrite(string hive, string path)
    {
        var combined = (hive + "\\" + path).ToLowerInvariant().Replace('/', '\\');
        foreach (var fragment in SensitiveFragments)
        {
            if (combined.Contains(fragment, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Registry writes to persistence/hijack keys are blocked ({fragment.Trim('\\')}). " +
                    "Use a dedicated admin tool if this change is intentional.");
        }
    }
}
