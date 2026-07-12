using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Startup;

namespace WindowsMcp.Services;

/// <summary>
/// Aggregates persistence/startup data from the registry, task scheduler, services, file
/// system, network stack, Winsock catalog and process list into a single
/// <see cref="StartupReportDto"/>. Each section is gathered independently; a section that
/// fails records an error and yields empty rather than failing the whole report.
/// </summary>
public sealed class StartupReportService : IStartupReportService
{
    private const string ApprovedRun = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
    private const string ApprovedFolder = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder";
    private const string InternetSettings = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";

    private static readonly (string Hive, string KeyPath, string? Approved)[] RunKeys =
    {
        ("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Run", ApprovedRun),
        ("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", null),
        ("HKLM", "Software\\Microsoft\\Windows\\CurrentVersion\\Run", ApprovedRun),
        ("HKLM", "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", null),
        ("HKLM", "Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", ApprovedRun),
    };

    private readonly IProcessService _process;
    private readonly IRegistryService _registry;
    private readonly IServiceControlService _services;
    private readonly ITaskSchedulerService _tasks;
    private readonly IFileSystemService _fs;
    private readonly ILspEnumerator _lsp;
    private readonly IAuthenticodeInspector _auth;
    private readonly IShortcutResolver _shortcuts;

    public StartupReportService(
        IProcessService process,
        IRegistryService registry,
        IServiceControlService services,
        ITaskSchedulerService tasks,
        IFileSystemService fs,
        ILspEnumerator lsp,
        IAuthenticodeInspector auth,
        IShortcutResolver shortcuts)
    {
        _process = process;
        _registry = registry;
        _services = services;
        _tasks = tasks;
        _fs = fs;
        _lsp = lsp;
        _auth = auth;
        _shortcuts = shortcuts;
    }

    public async Task<StartupReportDto> BuildAsync(bool includeProcesses = false, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var processes = includeProcesses
            ? await Safe(() => BuildProcessesAsync(ct), "processes", errors, Array.Empty<ProcessEntry>())
            : Array.Empty<ProcessEntry>();
        var run = await Safe(() => BuildRunEntriesAsync(ct), "run", errors, Array.Empty<RunEntry>());
        var folders = await Safe(() => BuildStartupFoldersAsync(ct), "startup_folders", errors, Array.Empty<StartupFolderEntry>());
        var tasks = await Safe(() => BuildTasksAsync(ct), "scheduled_tasks", errors, Array.Empty<StartupTaskEntry>());
        var services = await Safe(() => BuildServicesAsync(ct), "services", errors, Array.Empty<StartupServiceEntry>());
        var hosts = await Safe(() => BuildHostsAsync(ct), "hosts", errors, Array.Empty<HostsEntry>());
        var dns = await Safe(() => Task.FromResult(BuildDns()), "dns", errors, Array.Empty<DnsEntry>());
        var lsp = await Safe(() => Task.FromResult(BuildLsp()), "lsp", errors, Array.Empty<LspProviderEntry>());
        var shell = await Safe(() => BuildShellExtensionsAsync(ct), "shell_extensions", errors, Array.Empty<ShellExtensionEntry>());
        var cpl = await Safe(() => BuildControlPanelAppletsAsync(ct), "control_panel_applets", errors, Array.Empty<ControlPanelAppletEntry>());
        var ats = await Safe(() => BuildAccessibilityAsync(ct), "accessibility_tools", errors, Array.Empty<AccessibilityToolEntry>());
        var ifeo = await Safe(() => BuildIfeoAsync(ct), "image_file_execution_options", errors, Array.Empty<IfeoEntry>());
        var winlogon = await Safe(() => BuildWinlogonAsync(ct), "winlogon_hooks", errors, Array.Empty<WinlogonHookEntry>());
        var appinit = await Safe(() => BuildAppInitAsync(ct), "appinit_dlls", errors, Array.Empty<AppInitDllEntry>());
        var activeSetup = await Safe(() => BuildActiveSetupAsync(ct), "active_setup", errors, Array.Empty<ActiveSetupEntry>());
        var proxy = await Safe(() => BuildProxyAsync(ct), "browser_proxy", errors, Array.Empty<BrowserProxyEntry>());
        var zone = await Safe(() => BuildTrustedZoneAsync(ct), "trusted_zone", errors, Array.Empty<TrustedZoneEntry>());

        return new StartupReportDto(
            await BuildHeaderAsync(ct), processes, run, folders, tasks, services, hosts, dns, lsp, shell,
            cpl, ats, ifeo, winlogon, appinit, activeSetup, proxy, zone, errors.ToArray());
    }

    // ---- header -------------------------------------------------------------------------

    private async Task<StartupHeader> BuildHeaderAsync(CancellationToken ct)
    {
        string bootMode = GetSystemMetrics(SM_CLEANBOOT) switch { 1 => "FailSafe", 2 => "FailSafeWithNetwork", _ => "Normal" };
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string? defaultBrowser = null;
        try
        {
            var v = await _registry.GetAsync("HKCU",
                "Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\http\\UserChoice", "ProgId", ct);
            defaultBrowser = v.Data?.ToString();
        }
        catch { /* no UserChoice */ }

        return new StartupHeader(Environment.MachineName, Environment.OSVersion.VersionString,
            IsElevated(), DateTime.UtcNow, bootMode, user, defaultBrowser);
    }

    // ---- processes ----------------------------------------------------------------------

    private async Task<ProcessEntry[]> BuildProcessesAsync(CancellationToken ct)
    {
        var procs = await _process.ListAsync(null, ct);
        return procs.Select(p =>
        {
            var (t, s) = Sig(p.Path);
            return new ProcessEntry(p.Pid, p.Name, p.Path, p.MemoryMb, t, s);
        }).ToArray();
    }

    // ---- run keys (HKCU/HKLM/WOW + per-SID HKU) -----------------------------------------

    private async Task<RunEntry[]> BuildRunEntriesAsync(CancellationToken ct)
    {
        var list = new List<RunEntry>();
        foreach (var (hive, keyPath, approvedPath) in RunKeys)
            await AddRunKeyAsync(hive, hive, keyPath, approvedPath, list, ct);

        // Per-user hives for other / service accounts (skip the current user — already covered by HKCU).
        string? currentSid = TryGetCurrentUserSid();
        foreach (var sid in await _registry.EnumerateSubKeysAsync("HKU", "", ct))
        {
            if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
            if (!sid.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase)) continue;

            await AddRunKeyAsync($"HKU\\{sid}", "HKU",
                $"{sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                $"{sid}\\{ApprovedRun}", list, ct);
            await AddRunKeyAsync($"HKU\\{sid}", "HKU",
                $"{sid}\\Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", null, list, ct);
        }
        return list.ToArray();
    }

    private async Task AddRunKeyAsync(string displayHive, string hive, string keyPath, string? approvedPath,
        List<RunEntry> list, CancellationToken ct)
    {
        var approved = approvedPath is null
            ? new Dictionary<string, byte[]?>()
            : ToFlagMap(await _registry.EnumerateValuesAsync(hive, approvedPath, ct));

        foreach (var v in await _registry.EnumerateValuesAsync(hive, keyPath, ct))
        {
            string command = v.Data?.ToString() ?? string.Empty;
            bool enabled = !approved.TryGetValue(v.Name, out var flag) || StartupApproval.IsEnabled(flag);
            var (t, s) = Sig(CommandTarget.ResolveFullPath(command));
            list.Add(new RunEntry(displayHive, keyPath, v.Name, command, enabled, CommandTarget.Exists(command), t, s));
        }
    }

    // ---- startup folders ----------------------------------------------------------------

    private async Task<StartupFolderEntry[]> BuildStartupFoldersAsync(CancellationToken ct)
    {
        var userApproved = ToFlagMap(await _registry.EnumerateValuesAsync("HKCU", ApprovedFolder, ct));
        var commonApproved = ToFlagMap(await _registry.EnumerateValuesAsync("HKLM", ApprovedFolder, ct));

        var list = new List<StartupFolderEntry>();
        await AddFolderAsync("User", Environment.GetFolderPath(Environment.SpecialFolder.Startup), userApproved, list, ct);
        await AddFolderAsync("Common", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), commonApproved, list, ct);
        return list.ToArray();
    }

    private async Task AddFolderAsync(string scope, string folder, Dictionary<string, byte[]?> approved,
        List<StartupFolderEntry> list, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder)) return;
        string[] files;
        try { files = await _fs.ListAsync(folder, ct); }
        catch { return; }

        foreach (var file in files)
        {
            string full = Path.IsPathRooted(file) ? file : Path.Combine(folder, file);
            string name = Path.GetFileName(full);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            bool enabled = !approved.TryGetValue(name, out var flag) || StartupApproval.IsEnabled(flag);
            string? target = _shortcuts.ResolveTarget(full);
            var (t, s) = Sig(target);
            list.Add(new StartupFolderEntry(scope, name, target ?? full, enabled, target is not null && File.Exists(target), t, s));
        }
    }

    // ---- scheduled tasks ----------------------------------------------------------------

    private async Task<StartupTaskEntry[]> BuildTasksAsync(CancellationToken ct)
    {
        var tasks = await _tasks.ListDetailedAsync(ct);
        var list = new List<StartupTaskEntry>();
        foreach (var t in tasks)
        {
            bool startupTriggered = t.Triggers.Any(x =>
                x.Equals("Logon", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("Boot", StringComparison.OrdinalIgnoreCase));
            bool hasAction = !string.IsNullOrWhiteSpace(t.ActionPath);
            // No exec action (COM-handler task) => nothing to locate, so not "missing".
            bool targetExists = !hasAction || CommandTarget.Exists(t.ActionPath!);
            bool missingTarget = hasAction && !targetExists;
            if (!startupTriggered && !missingTarget) continue;

            var (tr, s) = Sig(CommandTarget.ResolveFullPath(t.ActionPath));
            list.Add(new StartupTaskEntry(t.Path, t.State, t.ActionPath, t.ActionArguments, t.Triggers, targetExists, tr, s));
        }
        return list.ToArray();
    }

    // ---- services -----------------------------------------------------------------------

    private async Task<StartupServiceEntry[]> BuildServicesAsync(CancellationToken ct)
    {
        var services = await _services.ListAsync(ct);
        var list = new List<StartupServiceEntry>();
        foreach (var s in services.Where(s => s.StartType is "Automatic" or "Boot" or "System"))
        {
            string? bin = null;
            try
            {
                var v = await _registry.GetAsync("HKLM", $"SYSTEM\\CurrentControlSet\\Services\\{s.Name}", "ImagePath", ct);
                bin = v.Data?.ToString();
            }
            catch { /* missing ImagePath */ }

            var (t, sig) = Sig(CommandTarget.ResolveFullPath(NormalizeImagePath(bin)));
            list.Add(new StartupServiceEntry(s.Name, s.DisplayName, s.Status, s.StartType, bin, t, sig));
        }
        return list.ToArray();
    }

    // ---- hosts / dns --------------------------------------------------------------------

    private async Task<HostsEntry[]> BuildHostsAsync(CancellationToken ct)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        string text = await _fs.ReadTextAsync(path, 1_000_000, "utf-8", ct);
        var list = new List<HostsEntry>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith('#')) break;
                list.Add(new HostsEntry(parts[0], parts[i]));
            }
        }
        return list.ToArray();
    }

    private static DnsEntry[] BuildDns() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => new DnsEntry(n.Name, a.ToString())))
            .ToArray();

    // ---- winsock lsp --------------------------------------------------------------------

    private LspProviderEntry[] BuildLsp() =>
        _lsp.Enumerate().Select(p =>
        {
            var (t, s) = Sig(p.ProviderPath);
            return new LspProviderEntry(p.CatalogEntryId, p.ProtocolName, p.ProviderPath, t, s);
        }).ToArray();

    // ---- shell extensions ---------------------------------------------------------------

    private async Task<ShellExtensionEntry[]> BuildShellExtensionsAsync(CancellationToken ct)
    {
        var list = new List<ShellExtensionEntry>();
        await AddOverlaysAsync(list, ct);
        await AddContextMenusAsync("*", "ContextMenu(*)", list, ct);
        await AddContextMenusAsync("Directory", "ContextMenu(Directory)", list, ct);
        await AddContextMenusAsync("Directory\\Background", "ContextMenu(Directory\\Background)", list, ct);
        await AddContextMenusAsync("Folder", "ContextMenu(Folder)", list, ct);
        return list.ToArray();
    }

    private async Task AddOverlaysAsync(List<ShellExtensionEntry> list, CancellationToken ct)
    {
        const string key = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers";
        foreach (var name in await _registry.EnumerateSubKeysAsync("HKLM", key, ct))
            list.Add(await BuildShellExtAsync("ShellIconOverlay", $"{key}\\{name}", name, ct));
    }

    private async Task AddContextMenusAsync(string classPath, string category, List<ShellExtensionEntry> list, CancellationToken ct)
    {
        string key = $"SOFTWARE\\Classes\\{classPath}\\shellex\\ContextMenuHandlers";
        foreach (var name in await _registry.EnumerateSubKeysAsync("HKLM", key, ct))
            list.Add(await BuildShellExtAsync(category, $"{key}\\{name}", name, ct));
    }

    private async Task<ShellExtensionEntry> BuildShellExtAsync(string category, string handlerKey, string subKeyName, CancellationToken ct)
    {
        string? def = await DefaultValueAsync("HKLM", handlerKey, ct);
        string? clsid = LooksLikeGuid(def) ? def : (LooksLikeGuid(subKeyName) ? subKeyName : null);
        string? dll = await ResolveClsidDllAsync(clsid, ct);
        var (t, s) = Sig(dll);
        return new ShellExtensionEntry(category, clsid ?? subKeyName, dll, t, s);
    }

    // ---- control panel applets ----------------------------------------------------------

    private async Task<ControlPanelAppletEntry[]> BuildControlPanelAppletsAsync(CancellationToken ct)
    {
        const string key = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Control Panel\\Cpls";
        var list = new List<ControlPanelAppletEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Registered applets (HKLM/HKCU "Control Panel\Cpls").
        foreach (var hive in new[] { "HKLM", "HKCU" })
        {
            foreach (var v in await _registry.EnumerateValuesAsync(hive, key, ct))
            {
                string path = Environment.ExpandEnvironmentVariables((v.Data?.ToString() ?? "").Trim('"'));
                var (t, s) = Sig(path);
                if (seen.Add(path))
                    list.Add(new ControlPanelAppletEntry(hive, v.Name, path, File.Exists(path), t, s));
            }
        }

        // Filesystem-resident applets that HiJackThis catches but the Cpls key omits
        // (e.g. vendor .cpl files dropped straight into System32 / SysWOW64).
        foreach (var (source, dir) in new[]
        {
            ("System32", Environment.GetFolderPath(Environment.SpecialFolder.System)),
            ("SysWOW64", Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)),
        })
        {
            string[] cpls;
            try { cpls = Directory.GetFiles(dir, "*.cpl"); }
            catch { continue; }

            foreach (var path in cpls)
            {
                if (!seen.Add(path)) continue;
                var (t, s) = Sig(path);
                list.Add(new ControlPanelAppletEntry(source, Path.GetFileName(path), path, true, t, s));
            }
        }
        return list.ToArray();
    }

    // ---- accessibility ATs --------------------------------------------------------------

    private async Task<AccessibilityToolEntry[]> BuildAccessibilityAsync(CancellationToken ct)
    {
        const string key = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Accessibility\\ATs";
        var list = new List<AccessibilityToolEntry>();
        foreach (var name in await _registry.EnumerateSubKeysAsync("HKLM", key, ct))
        {
            string? startExe = null;
            try { startExe = (await _registry.GetAsync("HKLM", $"{key}\\{name}", "StartExe", ct)).Data?.ToString(); }
            catch { /* no StartExe */ }

            // Most ATs subkeys are accessibility *feature settings* whose StartExe is a numeric
            // code, not a launchable program. Only report entries whose StartExe is an actual exe.
            if (string.IsNullOrWhiteSpace(startExe) ||
                !(startExe.Contains('\\') || startExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                continue;

            string exe = Environment.ExpandEnvironmentVariables(startExe);
            var (t, s) = Sig(CommandTarget.ResolveFullPath(exe));
            list.Add(new AccessibilityToolEntry(name, exe, CommandTarget.Exists(exe), t, s));
        }
        return list.ToArray();
    }

    // ---- image file execution options ---------------------------------------------------

    private async Task<IfeoEntry[]> BuildIfeoAsync(CancellationToken ct)
    {
        var list = new List<IfeoEntry>();
        foreach (var basePath in new[]
        {
            "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
            "SOFTWARE\\WOW6432Node\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
        })
        {
            foreach (var image in await _registry.EnumerateSubKeysAsync("HKLM", basePath, ct))
            {
                foreach (var kind in new[] { "Debugger", "VerifierDlls" })   // the hijack-relevant values
                {
                    string? val = null;
                    try { val = (await _registry.GetAsync("HKLM", $"{basePath}\\{image}", kind, ct)).Data?.ToString(); }
                    catch { /* value absent */ }
                    if (string.IsNullOrEmpty(val)) continue;
                    var (t, s) = Sig(CommandTarget.ResolveFullPath(val));
                    list.Add(new IfeoEntry(image, kind, val, CommandTarget.Exists(val), t, s));
                }
            }
        }
        return list.ToArray();
    }

    // ---- winlogon hooks -----------------------------------------------------------------

    private async Task<WinlogonHookEntry[]> BuildWinlogonAsync(CancellationToken ct)
    {
        const string key = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon";
        var list = new List<WinlogonHookEntry>();
        foreach (var name in new[] { "Shell", "Userinit", "Taskman", "VmApplet" })
        {
            string? val = null;
            try { val = (await _registry.GetAsync("HKLM", key, name, ct)).Data?.ToString(); }
            catch { /* value absent */ }
            if (string.IsNullOrEmpty(val)) continue;
            var (t, s) = Sig(CommandTarget.ResolveFullPath(val));
            list.Add(new WinlogonHookEntry(name, val, CommandTarget.Exists(val), t, s));
        }
        return list.ToArray();
    }

    // ---- AppInit_DLLs -------------------------------------------------------------------

    private async Task<AppInitDllEntry[]> BuildAppInitAsync(CancellationToken ct)
    {
        var list = new List<AppInitDllEntry>();
        foreach (var (scope, key) in new[]
        {
            ("64-bit", "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Windows"),
            ("WOW6432", "SOFTWARE\\WOW6432Node\\Microsoft\\Windows NT\\CurrentVersion\\Windows"),
        })
        {
            string? dlls = null;
            int load = 0;
            try { dlls = (await _registry.GetAsync("HKLM", key, "AppInit_DLLs", ct)).Data?.ToString(); } catch { }
            try { load = AsInt((await _registry.GetAsync("HKLM", key, "LoadAppInit_DLLs", ct)).Data); } catch { }
            if (string.IsNullOrWhiteSpace(dlls)) continue;

            foreach (var dll in dlls.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string resolved = Environment.ExpandEnvironmentVariables(dll);
                var (t, s) = Sig(resolved);
                list.Add(new AppInitDllEntry(scope, resolved, load != 0, File.Exists(resolved), t, s));
            }
        }
        return list.ToArray();
    }

    // ---- active setup -------------------------------------------------------------------

    private async Task<ActiveSetupEntry[]> BuildActiveSetupAsync(CancellationToken ct)
    {
        var list = new List<ActiveSetupEntry>();
        // HKLM is the usual location, but per-user HKCU entries are also an attacker vector.
        foreach (var (hive, basePath) in new[]
        {
            ("HKLM", "SOFTWARE\\Microsoft\\Active Setup\\Installed Components"),
            ("HKLM", "SOFTWARE\\WOW6432Node\\Microsoft\\Active Setup\\Installed Components"),
            ("HKCU", "SOFTWARE\\Microsoft\\Active Setup\\Installed Components"),
            ("HKCU", "SOFTWARE\\WOW6432Node\\Microsoft\\Active Setup\\Installed Components"),
        })
        {
            foreach (var comp in await _registry.EnumerateSubKeysAsync(hive, basePath, ct))
            {
                string? stub = await DefaultValueOrAsync(hive, $"{basePath}\\{comp}", "StubPath", ct);
                if (string.IsNullOrWhiteSpace(stub)) continue;
                string expanded = Environment.ExpandEnvironmentVariables(stub);
                var (t, s) = Sig(CommandTarget.ResolveFullPath(expanded));
                list.Add(new ActiveSetupEntry(hive, comp, expanded, CommandTarget.Exists(expanded), t, s));
            }
        }
        return list.ToArray();
    }

    // ---- browser proxy ------------------------------------------------------------------

    private async Task<BrowserProxyEntry[]> BuildProxyAsync(CancellationToken ct)
    {
        var list = new List<BrowserProxyEntry>();
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            bool enable = false; string? server = null, pac = null;
            try { enable = AsInt((await _registry.GetAsync(hive, InternetSettings, "ProxyEnable", ct)).Data) != 0; } catch { }
            try { server = (await _registry.GetAsync(hive, InternetSettings, "ProxyServer", ct)).Data?.ToString(); } catch { }
            try { pac = (await _registry.GetAsync(hive, InternetSettings, "AutoConfigURL", ct)).Data?.ToString(); } catch { }
            if (enable || !string.IsNullOrEmpty(server) || !string.IsNullOrEmpty(pac))
                list.Add(new BrowserProxyEntry(hive, enable, server, pac));
        }
        return list.ToArray();
    }

    // ---- trusted zone -------------------------------------------------------------------

    private async Task<TrustedZoneEntry[]> BuildTrustedZoneAsync(CancellationToken ct)
    {
        var list = new List<TrustedZoneEntry>();
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            string domainsKey = $"{InternetSettings}\\ZoneMap\\Domains";
            foreach (var domain in await _registry.EnumerateSubKeysAsync(hive, domainsKey, ct))
            {
                await AddZoneAsync(hive, $"{domainsKey}\\{domain}", domain, list, ct);
                foreach (var sub in await _registry.EnumerateSubKeysAsync(hive, $"{domainsKey}\\{domain}", ct))
                    await AddZoneAsync(hive, $"{domainsKey}\\{domain}\\{sub}", $"{sub}.{domain}", list, ct);
            }
        }
        return list.ToArray();
    }

    private async Task AddZoneAsync(string hive, string key, string label, List<TrustedZoneEntry> list, CancellationToken ct)
    {
        foreach (var v in await _registry.EnumerateValuesAsync(hive, key, ct))
        {
            int zone = AsInt(v.Data);
            // 1 = Local intranet, 2 = Trusted sites (non-default placements worth surfacing).
            if (zone is 1 or 2)
                list.Add(new TrustedZoneEntry(hive, $"{v.Name}://{label}", zone == 2 ? "Trusted" : "LocalIntranet"));
        }
    }

    // ---- shared helpers -----------------------------------------------------------------

    private (bool Trusted, string? Signer) Sig(string? path)
    {
        var info = _auth.Inspect(path);
        return (info.Trusted, info.Signer);
    }

    private async Task<string?> ResolveClsidDllAsync(string? clsid, CancellationToken ct)
    {
        if (!LooksLikeGuid(clsid)) return null;
        foreach (var basePath in new[] { "SOFTWARE\\Classes\\CLSID", "SOFTWARE\\WOW6432Node\\Classes\\CLSID" })
        {
            string? dll = await DefaultValueAsync("HKLM", $"{basePath}\\{clsid}\\InprocServer32", ct);
            if (!string.IsNullOrEmpty(dll))
                return Environment.ExpandEnvironmentVariables(dll.Trim('"'));
        }
        return null;
    }

    private async Task<string?> DefaultValueAsync(string hive, string path, CancellationToken ct)
    {
        var vals = await _registry.EnumerateValuesAsync(hive, path, ct);
        return vals.FirstOrDefault(v => v.Name == "(default)")?.Data?.ToString();
    }

    private async Task<string?> DefaultValueOrAsync(string hive, string path, string valueName, CancellationToken ct)
    {
        try
        {
            var v = await _registry.GetAsync(hive, path, valueName, ct);
            return v.Data?.ToString();
        }
        catch { return null; }
    }

    private static Dictionary<string, byte[]?> ToFlagMap(RegistryValueDto[] vals) =>
        vals.ToDictionary(v => v.Name, v => v.Data is byte[] b ? b : null, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a registry value as an int whether it came back boxed as int/long or a string DWORD.</summary>
    private static int AsInt(object? data) => data switch
    {
        int i => i,
        long l => (int)l,
        string s => int.TryParse(s, out var n) ? n : 0,
        _ => 0,
    };

    private static string? NormalizeImagePath(string? p) =>
        string.IsNullOrEmpty(p) ? p : (p.StartsWith("\\??\\") ? p[4..] : p);

    private static bool LooksLikeGuid(string? s) =>
        s is not null && s.StartsWith('{') && s.EndsWith('}') && Guid.TryParse(s, out _);

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string? TryGetCurrentUserSid()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value; }
        catch { return null; }
    }

    private static async Task<T> Safe<T>(Func<Task<T>> build, string section, List<string> errors, T fallback)
    {
        try { return await build(); }
        catch (Exception ex) { errors.Add($"{section}: {ex.GetType().Name}: {ex.Message}"); return fallback; }
    }

    private const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
