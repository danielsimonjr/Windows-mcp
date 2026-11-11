using Microsoft.Win32;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

// D-10 R14: every operation runs inside RegistryErrors.Guard, so access-denied is one
// UnauthorizedAccessException naming the hive and the key on every path — OpenSubKey alone
// reports it as a SecurityException, which the call-tool filter masks.
public sealed class RegistryService : IRegistryService
{
    public Task<RegistryValueDto> GetAsync(string hive, string path, string? valueName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        return Task.FromResult(RegistryErrors.Guard(hive, path, () =>
        {
            using var key = root.OpenSubKey(path)
                ?? throw new KeyNotFoundException($"Registry path not found: {hive}\\{path}");
            var data = valueName is null
                ? string.Join(",", key.GetValueNames())
                : key.GetValue(valueName);
            var kind = valueName is null ? "Names" : key.GetValueKind(valueName).ToString();
            return new RegistryValueDto(path, valueName ?? "(default)", data, kind);
        }));
    }

    public Task<RegistryValueDto[]> EnumerateValuesAsync(string hive, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        return Task.FromResult(RegistryErrors.Guard(hive, path, () =>
        {
            // An empty path means the hive root itself; never wrap the predefined base key in `using`.
            RegistryKey? key = path.Length == 0 ? root : root.OpenSubKey(path);
            if (key is null)
                return Array.Empty<RegistryValueDto>();
            try
            {
                return EnumerateValues(key, path);
            }
            finally { if (path.Length != 0) key.Dispose(); }
        }));
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
        return Task.FromResult(RegistryErrors.Guard(hive, path, () =>
        {
            if (path.Length == 0)
                return root.GetSubKeyNames();   // hive root; do not dispose the base key
            using var key = root.OpenSubKey(path);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }));
    }

    public Task SetAsync(string hive, string path, string valueName, object data, string kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        RegistryErrors.Guard(hive, path, () =>
        {
            using var key = root.CreateSubKey(path, writable: true)
                ?? throw new InvalidOperationException($"Cannot create or open key: {hive}\\{path}");
            var rk = kind switch
            {
                "String" => RegistryValueKind.String,
                "ExpandString" => RegistryValueKind.ExpandString,
                "DWord" => RegistryValueKind.DWord,
                "QWord" => RegistryValueKind.QWord,
                "Binary" => RegistryValueKind.Binary,
                "MultiString" => RegistryValueKind.MultiString,
                _ => RegistryValueKind.String   // safe default
            };
            key.SetValue(valueName, data, rk);
            return true;
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// C-2: the values and the immediate sub-key names of one key, in one read. An absent key is
    /// <see cref="KeyNotFoundException"/> (unlike the two enumerators, which answer an absent key
    /// with an empty array) — <c>registry_get</c> promises that message. An empty path lists the
    /// hive root.
    /// </summary>
    public Task<RegistryKeyDto> ListAsync(string hive, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        return Task.FromResult(RegistryErrors.Guard(hive, path, () =>
        {
            RegistryKey? key = path.Length == 0 ? root : root.OpenSubKey(path);
            if (key is null)
                throw new KeyNotFoundException($"Registry path not found: {hive}\\{path}");
            try
            {
                return new RegistryKeyDto(path, EnumerateValues(key, path), key.GetSubKeyNames());
            }
            finally { if (path.Length != 0) key.Dispose(); }
        }));
    }

    /// <summary>C-2: removes one value; false when the key or the value was not there.</summary>
    public Task<bool> DeleteValueAsync(string hive, string path, string valueName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = ResolveHive(hive);
        return Task.FromResult(RegistryErrors.Guard(hive, path, () =>
        {
            using var key = root.OpenSubKey(path, writable: true);
            if (key is null)
                return false;
            var existed = key.GetValueNames().Any(n => n.Equals(valueName, StringComparison.OrdinalIgnoreCase));
            if (existed)
                key.DeleteValue(valueName, throwOnMissingValue: false);
            return existed;
        }));
    }

    /// <summary>
    /// C-2: removes a key. The descendant count is walked first, so the result says what went
    /// and a key with sub-keys is refused (<see cref="InvalidOperationException"/> naming
    /// <c>recursive</c>) before anything is touched. A missing key is <c>Existed:false</c>. The
    /// tool layer's <see cref="RegistryGuard"/> keeps the hive root and the protected roots away
    /// from here; the service still refuses an empty path.
    /// </summary>
    public Task<RegistryKeyDeleteResult> DeleteKeyAsync(string hive, string path, bool recursive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (path.Trim().Length == 0)
            throw new ArgumentException("A hive root cannot be deleted; 'path' is empty", nameof(path));
        var root = ResolveHive(hive);

        return Task.FromResult(RegistryErrors.Guard(hive, path, () =>
        {
            int descendants;
            using (var key = root.OpenSubKey(path))
            {
                if (key is null)
                    return new RegistryKeyDeleteResult(false, 0);
                descendants = CountDescendants(key, hive, path);
            }

            if (descendants > 0 && !recursive)
                throw new InvalidOperationException(
                    $"'{hive}\\{path}' has {descendants} sub-key(s); pass recursive:true to delete the whole tree");

            if (descendants > 0)
                // R25: the count only proved the tree can be READ. A sub-key that denies DELETE
                // stops DeleteSubKeyTree after its siblings are gone, so say the delete may be
                // half done rather than report a clean refusal.
                RegistryErrors.Guard(hive, path, () =>
                {
                    root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    return true;
                }, partial: true);
            else
                root.DeleteSubKey(path, throwOnMissingSubKey: false);
            return new RegistryKeyDeleteResult(true, descendants);
        }));
    }

    /// <summary>
    /// Every key below <paramref name="key"/>. A descendant the caller may not read refuses the
    /// whole count naming THAT key (R25), before anything is deleted.
    /// </summary>
    private static int CountDescendants(RegistryKey key, string hive, string path)
    {
        int count = 0;
        foreach (var name in RegistryErrors.Guard(hive, path, key.GetSubKeyNames))
        {
            count++;
            var childPath = path.Length == 0 ? name : $"{path}\\{name}";
            using var child = RegistryErrors.Guard(hive, childPath, () => key.OpenSubKey(name));
            if (child is not null) count += CountDescendants(child, hive, childPath);
        }
        return count;
    }

    private static RegistryKey ResolveHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Registry.Users,
        _ => throw new ArgumentException($"Unknown hive: '{hive}'", nameof(hive))
    };
}
