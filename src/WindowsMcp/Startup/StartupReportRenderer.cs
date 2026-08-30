using System.Text;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Startup;

/// <summary>
/// Renders a <see cref="StartupReportDto"/> as a human-readable, section-grouped text block
/// (the companion to the structured JSON).
/// </summary>
public static class StartupReportRenderer
{
    public static string Render(StartupReportDto r)
    {
        var sb = new StringBuilder();
        var h = r.Header;
        sb.AppendLine("Windows-mcp Startup Report");
        sb.AppendLine($"Machine: {h.Machine}   OS: {h.OsVersion}   Elevated: {h.Elevated}   Boot: {h.BootMode}");
        sb.AppendLine($"User: {h.User}   DefaultBrowser: {h.DefaultBrowser ?? "(unknown)"}   (UTC {h.TimestampUtc:yyyy-MM-dd HH:mm:ss})");

        Section(sb, "Processes", r.Processes.Length);
        foreach (var p in r.Processes)
            sb.AppendLine($"  [{p.Pid}] {p.Name}  {p.Path ?? "(path n/a)"}  {Sig(p.Trusted, p.Signer)}");

        Section(sb, "Run entries", r.RunEntries.Length);
        foreach (var e in r.RunEntries)
            sb.AppendLine($"  [{e.Hive}\\{e.KeyPath}] {e.Name} = {e.Command}  {Flag("enabled", e.Enabled)} {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Startup folders", r.StartupFolders.Length);
        foreach (var e in r.StartupFolders)
            sb.AppendLine($"  [{e.Scope}] {e.FileName} -> {e.Target}  {Flag("enabled", e.Enabled)} {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Scheduled tasks", r.ScheduledTasks.Length);
        foreach (var t in r.ScheduledTasks)
            sb.AppendLine($"  {t.Path} [{t.State}] -> {t.ActionPath ?? "(no exec action)"}  triggers=[{string.Join(",", t.Triggers)}]  {Flag("target", t.TargetExists)} {Sig(t.Trusted, t.Signer)}");

        Section(sb, "Auto-start services", r.Services.Length);
        foreach (var s in r.Services)
            sb.AppendLine($"  {s.Name} ({s.DisplayName}) [{s.Status}/{s.StartType}] -> {s.BinaryPath ?? "(path n/a)"}  {Sig(s.Trusted, s.Signer)}");

        Section(sb, "Hosts file", r.Hosts.Length);
        foreach (var e in r.Hosts) sb.AppendLine($"  {e.Ip}  {e.Host}");

        Section(sb, "DNS servers", r.Dns.Length);
        foreach (var e in r.Dns) sb.AppendLine($"  [{e.Adapter}] {e.Server}");

        Section(sb, "Winsock LSP", r.Lsp.Length);
        foreach (var e in r.Lsp)
            sb.AppendLine($"  #{e.CatalogEntryId} {e.ProtocolName}  {e.ProviderPath ?? "(path n/a)"}  {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Shell extensions", r.ShellExtensions.Length);
        foreach (var e in r.ShellExtensions)
            sb.AppendLine($"  [{e.Category}] {e.Clsid} -> {e.Dll ?? "(dll n/a)"}  {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Control Panel applets", r.ControlPanelApplets.Length);
        foreach (var e in r.ControlPanelApplets)
            sb.AppendLine($"  [{e.Source}] {e.Name} -> {e.Path}  {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Accessibility tools", r.AccessibilityTools.Length);
        foreach (var e in r.AccessibilityTools)
            sb.AppendLine($"  {e.Name} -> {e.StartExe ?? "(none)"}  {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Image File Execution Options", r.ImageFileExecutionOptions.Length);
        foreach (var e in r.ImageFileExecutionOptions)
            sb.AppendLine($"  {e.Image} [{e.Kind}] = {e.Value}  {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Winlogon hooks", r.WinlogonHooks.Length);
        foreach (var e in r.WinlogonHooks)
            sb.AppendLine($"  {e.Name} = {e.Value}  {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "AppInit_DLLs", r.AppInitDlls.Length);
        foreach (var e in r.AppInitDlls)
            sb.AppendLine($"  [{e.Scope}] {e.Dll}  {Flag("loadEnabled", e.Enabled)} {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Active Setup", r.ActiveSetup.Length);
        foreach (var e in r.ActiveSetup)
            sb.AppendLine($"  [{e.Hive}] {e.Component} -> {e.StubPath}  {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Browser proxy", r.BrowserProxy.Length);
        foreach (var e in r.BrowserProxy)
            sb.AppendLine($"  [{e.Hive}] {Flag("enabled", e.ProxyEnable)} server={e.ProxyServer ?? "(none)"} pac={e.AutoConfigUrl ?? "(none)"}");

        Section(sb, "Trusted/zoned sites", r.TrustedZone.Length);
        foreach (var e in r.TrustedZone)
            sb.AppendLine($"  [{e.Hive}] {e.Domain}  zone={e.Zone}");

        if (r.Errors.Length > 0)
        {
            Section(sb, "Errors", r.Errors.Length);
            foreach (var e in r.Errors) sb.AppendLine($"  {e}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A compact, inline-friendly view: per-section counts plus only the entries that warrant
    /// attention (untrusted code-signing or a missing target), and any proxy / trusted-zone
    /// configuration. Keeps the response small instead of dumping every entry.
    /// </summary>
    public static string RenderSummary(StartupReportDto r)
    {
        var sb = new StringBuilder();
        var h = r.Header;
        sb.AppendLine("Windows-mcp Startup Report — SUMMARY");
        sb.AppendLine($"Machine: {h.Machine}   OS: {h.OsVersion}   Elevated: {h.Elevated}   Boot: {h.BootMode}");
        sb.AppendLine($"User: {h.User}   DefaultBrowser: {h.DefaultBrowser ?? "(unknown)"}   (UTC {h.TimestampUtc:yyyy-MM-dd HH:mm:ss})");

        sb.AppendLine();
        sb.AppendLine("== Section counts ==");
        sb.AppendLine($"  processes={r.Processes.Length} run={r.RunEntries.Length} startupFolders={r.StartupFolders.Length} " +
                      $"tasks={r.ScheduledTasks.Length} services={r.Services.Length} hosts={r.Hosts.Length} dns={r.Dns.Length}");
        sb.AppendLine($"  lsp={r.Lsp.Length} shellExt={r.ShellExtensions.Length} controlPanel={r.ControlPanelApplets.Length} " +
                      $"accessibility={r.AccessibilityTools.Length} ifeo={r.ImageFileExecutionOptions.Length} winlogon={r.WinlogonHooks.Length}");
        sb.AppendLine($"  appInitDlls={r.AppInitDlls.Length} activeSetup={r.ActiveSetup.Length} proxy={r.BrowserProxy.Length} " +
                      $"trustedZone={r.TrustedZone.Length} errors={r.Errors.Length}");

        var high = new List<string>();
        var medium = new List<string>();
        var low = new List<string>();
        void Consider(bool trusted, bool targetExists, string line, string? pathHint = null, bool persistenceHook = false)
        {
            if (trusted && targetExists) return;
            if (persistenceHook)
            {
                high.Add($"[HIGH persistence-hook] {line}");
                return;
            }
            if (!targetExists)
            {
                var ms = LooksLikeMicrosoftPath(pathHint);
                if (ms)
                    low.Add($"[LOW ms-file-missing] {line}");
                else
                    high.Add($"[HIGH missing-target] {line}");
                return;
            }
            medium.Add($"[MEDIUM untrusted-third-party] {line}");
        }
        foreach (var e in r.Processes) Consider(e.Trusted, true, $"[process] {e.Name} {e.Path}", e.Path);
        foreach (var e in r.RunEntries) Consider(e.Trusted, e.TargetExists, $"[run] {e.Hive} {e.Name} = {e.Command}", e.Command);
        foreach (var e in r.StartupFolders) Consider(e.Trusted, e.TargetExists, $"[startupFolder] {e.Scope} {e.FileName} -> {e.Target}", e.Target);
        foreach (var e in r.ScheduledTasks)
            if (!string.IsNullOrEmpty(e.ActionPath)) Consider(e.Trusted, e.TargetExists, $"[task] {e.Path} -> {e.ActionPath}", e.ActionPath);
        foreach (var e in r.Services) Consider(e.Trusted, true, $"[service] {e.Name} -> {e.BinaryPath}", e.BinaryPath);
        foreach (var e in r.Lsp) Consider(e.Trusted, true, $"[lsp] #{e.CatalogEntryId} {e.ProtocolName} -> {e.ProviderPath}", e.ProviderPath);
        foreach (var e in r.ShellExtensions) Consider(e.Trusted, true, $"[shellExt] {e.Category} {e.Clsid} -> {e.Dll}", e.Dll);
        foreach (var e in r.ControlPanelApplets) Consider(e.Trusted, e.TargetExists, $"[cpl] {e.Source} {e.Name} -> {e.Path}", e.Path);
        foreach (var e in r.AccessibilityTools) Consider(e.Trusted, e.TargetExists, $"[at] {e.Name} -> {e.StartExe}", e.StartExe);
        foreach (var e in r.ImageFileExecutionOptions) Consider(e.Trusted, e.TargetExists, $"[ifeo] {e.Image} [{e.Kind}] = {e.Value}", e.Value, persistenceHook: true);
        foreach (var e in r.WinlogonHooks) Consider(e.Trusted, e.TargetExists, $"[winlogon] {e.Name} = {e.Value}", e.Value, persistenceHook: true);
        foreach (var e in r.AppInitDlls) Consider(e.Trusted, e.TargetExists, $"[appinit] {e.Scope} {e.Dll}", e.Dll, persistenceHook: true);
        foreach (var e in r.ActiveSetup) Consider(e.Trusted, e.TargetExists, $"[activeSetup] {e.Hive} {e.Component} -> {e.StubPath}", e.StubPath);

        sb.AppendLine();
        sb.AppendLine($"== Flagged HIGH missing-target / persistence hooks ({high.Count}) ==");
        foreach (var f in high) sb.AppendLine($"  {f}");
        sb.AppendLine($"== Flagged MEDIUM untrusted-third-party ({medium.Count}) ==");
        foreach (var f in medium) sb.AppendLine($"  {f}");
        sb.AppendLine($"== Flagged LOW ms-file-missing ({low.Count}) ==");
        foreach (var f in low) sb.AppendLine($"  {f}");

        if (r.BrowserProxy.Length > 0 || r.TrustedZone.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"== Network / zone (review) ({r.BrowserProxy.Length + r.TrustedZone.Length}) ==");
            foreach (var e in r.BrowserProxy)
                sb.AppendLine($"  [proxy] {e.Hive} enabled={(e.ProxyEnable ? "Y" : "N")} server={e.ProxyServer ?? "(none)"} pac={e.AutoConfigUrl ?? "(none)"}");
            foreach (var e in r.TrustedZone)
                sb.AppendLine($"  [zone] {e.Hive} {e.Domain} = {e.Zone}");
        }

        if (r.Errors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"== Errors ({r.Errors.Length}) ==");
            foreach (var e in r.Errors) sb.AppendLine($"  {e}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void Section(StringBuilder sb, string title, int count)
    {
        sb.AppendLine();
        sb.AppendLine($"== {title} ({count}) ==");
    }

    private static string Flag(string name, bool value) => $"{name}={(value ? "Y" : "N")}";

    private static string Sig(bool trusted, string? signer) =>
        trusted ? $"trusted={(signer is null ? "Y" : signer)}" : "trusted=N";

    internal static bool LooksLikeMicrosoftPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Contains(@"\windows\system32\")
            || p.Contains(@"\windows\syswow64\")
            || p.Contains(@"\windows\winsxs\")
            || p.Contains(@"\program files\windows")
            || p.Contains(@"\program files (x86)\windows");
    }
}
