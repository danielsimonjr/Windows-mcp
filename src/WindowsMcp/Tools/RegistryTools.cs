using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class RegistryTools
{
    private readonly IRegistryService _registry;

    public RegistryTools(IRegistryService registry)
    {
        _registry = registry;
    }

    [McpServerTool(Title = "Read registry", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Read a Windows registry value, or list a key. With value_name: that value's data and kind. " +
                 "Without it: the key's values (name, data, kind) and its immediate sub-key names as " +
                 "{Path, Values:[{Path, Name, Data, Kind}], SubKeys:[...]}; an empty path lists the hive root. " +
                 "A missing key is an error naming the path.")]
    public async Task<string> RegistryGet(
        [Description("Hive: HKCU, HKLM, HKCR, HKU")] string hive,
        [Description("Subkey path like 'Software\\Microsoft\\Windows'")] string path,
        [Description("Specific value name; if omitted, lists the key's values and sub-keys")] string? value_name = null,
        CancellationToken ct = default)
    {
        if (value_name is null)
            return JsonSerializer.Serialize(await _registry.ListAsync(hive, path, ct));
        var result = await _registry.GetAsync(hive, path, value_name, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Title = "Write registry", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Write a Windows registry value. Requires confirm: true.")]
    public async Task<string> RegistrySet(
        [Description("Hive: HKCU, HKLM, HKCR, HKU")] string hive,
        [Description("Subkey path like 'Software\\Microsoft\\Windows'")] string path,
        [Description("Value name to set")] string value_name,
        [Description("Data to write")] string data,
        [Description("Registry value kind: String, DWord, QWord, Binary, MultiString, ExpandString")] string kind,
        [Description("Must be true to confirm the registry write")] bool confirm = false)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for registry writes");
        await _registry.SetAsync(hive, path, value_name, data, kind);
        return $"set {hive}\\{path}\\{value_name}";
    }

    [McpServerTool(Title = "Delete registry key or value", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Delete a Windows registry value or key. Requires confirm: true. With value_name: removes that " +
                 "one value. Without it: removes the key itself — a key that has sub-keys also needs " +
                 "recursive: true, and the hive root and the profile/OS roots (Software, Software\\Microsoft, " +
                 "System, SYSTEM\\CurrentControlSet, Environment, ...) are refused outright. Deleting what is " +
                 "not there is not an error: existed:false. Returns {hive, path, valueName?, deleted, existed, " +
                 "subKeysRemoved?} — subKeysRemoved counts the descendant keys removed with the key. A key the " +
                 "server may not change (most of HKLM, unless the server runs elevated) is refused as access " +
                 "denied naming the key; a tree delete denied part-way says sub-keys may already be gone.")]
    public async Task<string> RegistryDelete(
        [Description("Hive: HKCU, HKLM, HKCR, HKU")] string hive,
        [Description("Subkey path like 'Software\\MyApp'")] string path,
        [Description("Value name to delete; omit to delete the key itself")] string? value_name = null,
        [Description("Delete the key's sub-keys too (required when it has any)")] bool recursive = false,
        [Description("Must be true to confirm the registry delete")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for registry deletes");

        if (value_name is null)
        {
            if (RegistryGuard.Refusal(path) is { } refusal)
                throw new ArgumentException(refusal, nameof(path));
            var key = await _registry.DeleteKeyAsync(hive, path, recursive, ct);
            return JsonSerializer.Serialize(new
            {
                hive,
                path,
                deleted = key.Existed,
                existed = key.Existed,
                subKeysRemoved = key.SubKeysRemoved,
            });
        }

        var existedValue = await _registry.DeleteValueAsync(hive, path, value_name, ct);
        return JsonSerializer.Serialize(new
        {
            hive,
            path,
            valueName = value_name,
            deleted = existedValue,
            existed = existedValue,
        });
    }
}
