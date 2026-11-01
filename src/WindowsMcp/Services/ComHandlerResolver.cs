using Microsoft.Win32;

namespace WindowsMcp.Services;

/// <summary>
/// Resolves a scheduled-task COM-handler CLSID to its InprocServer32 DLL path
/// so startup reports can sign-check handler binaries instead of treating them
/// as "no action path."
/// </summary>
internal static class ComHandlerResolver
{
    public static string? Resolve(Guid clsid)
    {
        var keyPath = $@"CLSID\{clsid:B}\InprocServer32";
        using var key = Registry.ClassesRoot.OpenSubKey(keyPath);
        var value = key?.GetValue(null)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value);
    }
}
