using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class StartupReportServiceTests
{
    private const string RunHkcu = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ApprovedHkcu = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";

    private sealed class Fakes
    {
        public readonly Mock<IProcessService> Process = new();
        public readonly Mock<IRegistryService> Registry = new();
        public readonly Mock<IServiceControlService> Services = new();
        public readonly Mock<ITaskSchedulerService> Tasks = new();
        public readonly Mock<IFileSystemService> Fs = new();
        public readonly Mock<ILspEnumerator> Lsp = new();
        public readonly Mock<IAuthenticodeInspector> Auth = new();
        public readonly Mock<IShortcutResolver> Shortcuts = new();

        public Fakes()
        {
            Process.Setup(x => x.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProcessDto>());
            Registry.Setup(x => x.EnumerateValuesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<RegistryValueDto>());
            Registry.Setup(x => x.EnumerateSubKeysAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());
            Registry.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RegistryValueDto("p", "ImagePath", null, "String"));
            Services.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ServiceDto>());
            Tasks.Setup(x => x.ListDetailedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScheduledTaskDetailDto>());
            Fs.Setup(x => x.ReadTextAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("");
            Fs.Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
            Lsp.Setup(x => x.Enumerate()).Returns(Array.Empty<LspProviderDto>());
            Auth.Setup(x => x.Inspect(It.IsAny<string?>())).Returns(new AuthenticodeInfo(false, null));
            Shortcuts.Setup(x => x.ResolveTarget(It.IsAny<string>())).Returns<string>(p => p);
        }

        public StartupReportService Build() => new(
            Process.Object, Registry.Object, Services.Object, Tasks.Object,
            Fs.Object, Lsp.Object, Auth.Object, Shortcuts.Object);
    }

    [Fact]
    public async Task RunEntries_join_StartupApproved_for_enabled_state()
    {
        var f = new Fakes();
        f.Registry.Setup(x => x.EnumerateValuesAsync("HKCU", RunHkcu, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new RegistryValueDto(RunHkcu, "Zoom", "zoom.exe", "String"),
                new RegistryValueDto(RunHkcu, "Keep", "keep.exe", "String"),
            });
        f.Registry.Setup(x => x.EnumerateValuesAsync("HKCU", ApprovedHkcu, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new RegistryValueDto(ApprovedHkcu, "Zoom", new byte[] { 3, 0, 0, 0 }, "Binary") });

        var report = await f.Build().BuildAsync();

        report.RunEntries.Single(e => e.Name == "Zoom" && e.Hive == "HKCU").Enabled.Should().BeFalse();
        report.RunEntries.Single(e => e.Name == "Keep" && e.Hive == "HKCU").Enabled.Should().BeTrue();
        report.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task WowRun_entries_use_the_standard_StartupApproved_key()
    {
        const string wowRun = "Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run";
        var f = new Fakes();
        f.Registry.Setup(x => x.EnumerateValuesAsync("HKLM", wowRun, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new RegistryValueDto(wowRun, "OldApp", "old.exe", "String") });
        // The disabled flag lives in the single (non-WOW) StartupApproved\Run key under HKLM.
        f.Registry.Setup(x => x.EnumerateValuesAsync("HKLM", ApprovedHkcu, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new RegistryValueDto(ApprovedHkcu, "OldApp", new byte[] { 3, 0, 0, 0 }, "Binary") });

        var report = await f.Build().BuildAsync();

        report.RunEntries.Single(e => e.Name == "OldApp").Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Services_only_includes_autostart_and_applies_signer()
    {
        var f = new Fakes();
        f.Services.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ServiceDto("AutoSvc", "Auto Service", "Running", "Automatic"),
            new ServiceDto("ManualSvc", "Manual Service", "Stopped", "Manual"),
        });
        f.Auth.Setup(x => x.Inspect(It.IsAny<string?>())).Returns(new AuthenticodeInfo(true, "CN=Test"));

        var report = await f.Build().BuildAsync();

        report.Services.Should().ContainSingle(s => s.Name == "AutoSvc");
        report.Services.Should().NotContain(s => s.Name == "ManualSvc");
        report.Services.Single().Trusted.Should().BeTrue();
    }

    [Fact]
    public async Task Tasks_includes_logon_or_missing_target_and_excludes_the_rest()
    {
        var f = new Fakes();
        string realExe = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        f.Tasks.Setup(x => x.ListDetailedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ScheduledTaskDetailDto("Logon", "\\Logon", "Ready", realExe, null, new[] { "Logon" }),
            new ScheduledTaskDetailDto("TimeOnly", "\\TimeOnly", "Ready", realExe, null, new[] { "Daily" }),
            new ScheduledTaskDetailDto("Dead", "\\Dead", "Ready", @"C:\nope\missing_wmcp.exe", null, new[] { "Daily" }),
        });

        var report = await f.Build().BuildAsync();

        report.ScheduledTasks.Select(t => t.Path).Should().BeEquivalentTo("\\Logon", "\\Dead");
        report.ScheduledTasks.Single(t => t.Path == "\\Dead").TargetExists.Should().BeFalse();
    }

    [Fact]
    public async Task Task_with_no_exec_action_is_not_marked_missing_target()
    {
        var f = new Fakes();
        f.Tasks.Setup(x => x.ListDetailedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            // COM-handler logon task: no ExecAction => ActionPath null.
            new ScheduledTaskDetailDto("ComTask", "\\MS\\ComTask", "Ready", null, null, new[] { "Logon" }),
        });

        var report = await f.Build().BuildAsync();

        report.ScheduledTasks.Single(t => t.Path == "\\MS\\ComTask").TargetExists.Should().BeTrue();
    }

    [Fact]
    public async Task ControlPanel_scans_System32_for_cpl_files_not_just_the_registry()
    {
        // Registry Cpls key is empty (default mock), so any applets come from the filesystem scan.
        var report = await new Fakes().Build().BuildAsync();

        report.ControlPanelApplets.Should().Contain(a =>
            a.Source == "System32" && a.Path.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Accessibility_reports_only_real_exe_ATs_not_setting_codes()
    {
        const string atKey = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Accessibility\\ATs";
        var f = new Fakes();
        f.Registry.Setup(x => x.EnumerateSubKeysAsync("HKLM", atKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "narrator", "animations" });
        f.Registry.Setup(x => x.GetAsync("HKLM", $"{atKey}\\narrator", "StartExe", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto(atKey, "StartExe", "C:\\Windows\\System32\\Narrator.exe", "String"));
        f.Registry.Setup(x => x.GetAsync("HKLM", $"{atKey}\\animations", "StartExe", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto(atKey, "StartExe", "13", "String"));   // setting code, not an exe

        var report = await f.Build().BuildAsync();

        report.AccessibilityTools.Select(a => a.Name).Should().BeEquivalentTo("narrator");
    }

    [Fact]
    public async Task Proxy_parses_proxyenable_stored_as_a_string_dword()
    {
        const string internetSettings = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
        var f = new Fakes();
        f.Registry.Setup(x => x.GetAsync("HKCU", internetSettings, "ProxyEnable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto(internetSettings, "ProxyEnable", "1", "String"));   // string, not int

        var report = await f.Build().BuildAsync();

        report.BrowserProxy.Should().Contain(p => p.Hive == "HKCU" && p.ProxyEnable);
    }

    [Fact]
    public async Task Section_failure_is_isolated_and_other_sections_still_populate()
    {
        var f = new Fakes();
        f.Process.Setup(x => x.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        f.Lsp.Setup(x => x.Enumerate()).Returns(new[] { new LspProviderDto(1, "MSAFD", @"C:\x.dll") });

        var report = await f.Build().BuildAsync(includeProcesses: true);

        report.Processes.Should().BeEmpty();
        report.Errors.Should().Contain(e => e.StartsWith("processes:"));
        report.Lsp.Should().ContainSingle(p => p.ProtocolName == "MSAFD");
    }
}
