using Microsoft.Win32;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class RegistryService : IRegistryService
{
    public Task<RegistryValueDto> GetAsync(string hive, string path, string? valueName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        using var key = root.OpenSubKey(path)
            ?? throw new KeyNotFoundException($"Registry path not found: {hive}\\{path}");
        var data = valueName is null
            ? string.Join(",", key.GetValueNames())
            : key.GetValue(valueName);
        var kind = valueName is null ? "Names" : key.GetValueKind(valueName).ToString();
        return Task.FromResult(new RegistryValueDto(path, valueName ?? "(default)", data, kind));
    }

    public Task<RegistryValueDto[]> EnumerateValuesAsync(string hive, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        // An empty path means the hive root itself; never wrap the predefined base key in `using`.
        RegistryKey? key = path.Length == 0 ? root : root.OpenSubKey(path);
        if (key is null)
            return Task.FromResult(Array.Empty<RegistryValueDto>());
        try
        {
            return Task.FromResult(EnumerateValues(key, path));
        }
        finally { if (path.Length != 0) key.Dispose(); }
    }

    private static RegistryValueDto[] EnumerateValues(RegistryKey key, string path)
    {
        var values = key.GetValueNames()
            .Select(name => new RegistryValueDto(
                path,
                name.Length == 0 ? "(default)" : name,
                key.GetValue(name),
                key.GetValueKind(name).ToString()))
            .ToArray();
        return values;
    }

    public Task<string[]> EnumerateSubKeysAsync(string hive, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        if (path.Length == 0)
            return Task.FromResult(root.GetSubKeyNames());   // hive root; do not dispose the base key
        using var key = root.OpenSubKey(path);
        return Task.FromResult(key?.GetSubKeyNames() ?? Array.Empty<string>());
    }

    public Task SetAsync(string hive, string path, string valueName, object data, string kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RegistryPolicy.ThrowIfSensitiveWrite(hive, path);
        var root = ResolveHive(hive);
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Cannot create or open key: {hive}\\{path}");
        var rk = kind switch
        {
            "String"      => RegistryValueKind.String,
            "ExpandString"=> RegistryValueKind.ExpandString,
            "DWord"       => RegistryValueKind.DWord,
            "QWord"       => RegistryValueKind.QWord,
            "Binary"      => RegistryValueKind.Binary,
            "MultiString" => RegistryValueKind.MultiString,
            _             => throw new ArgumentException(
                $"Unknown registry value kind '{kind}'; expected String|ExpandString|DWord|QWord|Binary|MultiString")
        };
        key.SetValue(valueName, data, rk);
        return Task.CompletedTask;
    }

    private static RegistryKey ResolveHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER"   => Registry.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE"  => Registry.LocalMachine,
        "HKCR" or "HKEY_CLASSES_ROOT"   => Registry.ClassesRoot,
        "HKU"  or "HKEY_USERS"          => Registry.Users,
        _ => throw new ArgumentException($"Unknown hive: '{hive}'", nameof(hive))
    };
}
