using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class SystemTools
{
    private readonly IWmiService _wmi;
    private readonly IEnvService _env;
    private readonly IPowerService _power;
    private readonly INotificationService _notification;
    private readonly IAudioService _audio;
    private readonly ISecurityService _security;
    private readonly IReliabilityService _reliability;
    private readonly IDriverService _drivers;

    public SystemTools(
        IWmiService wmi,
        IEnvService env,
        IPowerService power,
        INotificationService notification,
        IAudioService audio,
        ISecurityService security,
        IReliabilityService reliability,
        IDriverService drivers)
    {
        _wmi = wmi;
        _env = env;
        _power = power;
        _notification = notification;
        _audio = audio;
        _security = security;
        _reliability = reliability;
        _drivers = drivers;
    }

    private static readonly Dictionary<string, string> WmiClassMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["os"]      = "Win32_OperatingSystem",
        ["memory"]  = "Win32_PhysicalMemory",
        ["disk"]    = "Win32_LogicalDisk",
        ["gpu"]     = "Win32_VideoController",
        ["battery"] = "Win32_Battery",
    };

    [McpServerTool, Description("Query system information via WMI. category: os|memory|disk|gpu|battery.")]
    public async Task<string> SystemInfo(
        [Description("Category of system info: os, memory, disk, gpu, battery")] string category,
        CancellationToken ct = default)
    {
        if (!WmiClassMap.TryGetValue(category, out var className))
            throw new ArgumentException($"Unknown category '{category}'; expected os|memory|disk|gpu|battery");

        var rows = await _wmi.QueryAsync(className, ct: ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool, Description("Control audio. action: get|set|mute|unmute. 'set' requires level (0-100).")]
    public async Task<string> Audio(
        [Description("Action: get, set, mute, unmute")] string action,
        [Description("Volume level 0-100 (required for set)")] int? level = null,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "get":
                var state = await _audio.GetAsync(ct);
                return JsonSerializer.Serialize(state);

            case "set":
                if (!level.HasValue)
                    throw new ArgumentException("'set' requires level");
                if (level.Value is < 0 or > 100)
                    throw new ArgumentException("'level' must be between 0 and 100");
                await _audio.SetVolumeAsync(level.Value, ct);
                return $"volume set to {level.Value}";

            case "mute":
                await _audio.SetMutedAsync(true, ct);
                return "muted";

            case "unmute":
                await _audio.SetMutedAsync(false, ct);
                return "unmuted";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected get|set|mute|unmute");
        }
    }

    [McpServerTool, Description("Show a Windows toast notification.")]
    public async Task<string> Notification(
        [Description("Notification title")] string title,
        [Description("Notification message body")] string message,
        CancellationToken ct = default)
    {
        await _notification.ShowAsync(title, message, ct);
        return "notification shown";
    }

    [McpServerTool, Description("Run a Windows security audit and return firewall, Defender, UAC, and BitLocker status. Probes that require admin (Get-BitLockerVolume, some firewall profiles) return null when run unelevated; non-null fields are still useful.")]
    public async Task<string> SecurityAudit(CancellationToken ct = default)
    {
        var audit = await _security.AuditAsync(ct);
        return JsonSerializer.Serialize(audit);
    }

    [McpServerTool, Description("Report system stability: crash minidumps in C:\\Windows\\Minidump (name, size, time) plus recent reliability failure records (app/OS/hardware failures). Useful for investigating BSODs and instability.")]
    public async Task<string> Reliability(
        [Description("Max reliability failure records to return (default 50)")] int max_records = 50,
        CancellationToken ct = default)
    {
        var report = await _reliability.GetAsync(max_records, ct);
        return JsonSerializer.Serialize(report);
    }

    [McpServerTool, Description("List installed PnP device drivers with version, date, manufacturer, signed-state, and INF name. Old or unsigned drivers are a real attack surface (BYOVD - bring-your-own-vulnerable-driver).")]
    public async Task<string> DriverList(CancellationToken ct = default)
    {
        var drivers = await _drivers.ListAsync(ct);
        return JsonSerializer.Serialize(drivers);
    }

    [McpServerTool, Description("Run a raw WMI query. class_name: WMI class, e.g. Win32_OperatingSystem.")]
    public async Task<string> WmiQuery(
        [Description("WMI class name, e.g. Win32_Process")] string class_name,
        [Description("WMI namespace (default: root\\cimv2)")] string? @namespace = null,
        [Description("WHERE clause, e.g. ProcessId=1234")] string? where = null,
        CancellationToken ct = default)
    {
        var rows = await _wmi.QueryAsync(class_name, @namespace, where, ct);
        return JsonSerializer.Serialize(rows);
    }

    // Env vars whose NAME matches any of these (case-insensitive) are redacted in
    // list/get output unless include_secrets:true. Developer machines routinely
    // have API keys + tokens in the process environment; a default list dump would
    // leak them into LLM transcripts. Tokens still pass through Set as plain values
    // since the caller supplied them explicitly.
    private static readonly string[] SecretNamePatterns =
    {
        "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "AUTH",
        "CREDENTIAL", "PRIVATE", "API_KEY", "APIKEY", "ACCESS_TOKEN", "PAT"
    };

    private static bool IsSecretName(string name)
    {
        foreach (var pat in SecretNamePatterns)
        {
            if (name.Contains(pat, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [McpServerTool, Description("Get, set, or list environment variables. action: get|set|list. scope: Process|User|Machine. 'set' requires confirm:true. By default values for variables whose name contains KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL/PRIVATE/PAT are redacted; pass include_secrets:true to return them raw.")]
    public async Task<string> Env(
        [Description("Action: get, set, list")] string action,
        [Description("Variable name (required for get/set)")] string? name = null,
        [Description("Variable value (for set; null to delete)")] string? value = null,
        [Description("Scope: Process, User, Machine")] string scope = "Process",
        [Description("Must be true to confirm set/delete")] bool confirm = false,
        [Description("Set true to return raw values for secret-named vars. Default false redacts them as '***REDACTED***'.")] bool include_secrets = false,
        CancellationToken ct = default)
    {
        var target = scope.ToLowerInvariant() switch
        {
            "process" => EnvironmentVariableTarget.Process,
            "user"    => EnvironmentVariableTarget.User,
            "machine" => EnvironmentVariableTarget.Machine,
            _         => throw new ArgumentException($"Unknown scope '{scope}'; expected Process|User|Machine")
        };

        switch (action.ToLowerInvariant())
        {
            case "get":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'get' requires name");
                var val = await _env.GetAsync(name, target, ct);
                if (val is not null && !include_secrets && IsSecretName(name))
                    val = "***REDACTED***";
                return JsonSerializer.Serialize(val);

            case "set":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for set");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'set' requires name");
                await _env.SetAsync(name, value, target, ct);
                return value is null ? $"deleted '{name}' from {scope}" : $"set '{name}' in {scope}";

            case "list":
                var vars = await _env.ListAsync(target, ct);
                if (!include_secrets)
                {
                    var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in vars)
                        redacted[kv.Key] = IsSecretName(kv.Key) ? "***REDACTED***" : kv.Value;
                    vars = redacted;
                }
                return JsonSerializer.Serialize(vars);

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected get|set|list");
        }
    }

    [McpServerTool, Description("Execute a system power action. action: shutdown|reboot|logoff|lock|sleep|hibernate. Requires confirm:true.")]
    public async Task<string> PowerAction(
        [Description("Power action: shutdown, reboot, logoff, lock, sleep, hibernate")] string action,
        [Description("Must be true to confirm the power action")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for power actions");

        await _power.ExecuteAsync(action, ct);
        return $"power action '{action}' executed";
    }
}
