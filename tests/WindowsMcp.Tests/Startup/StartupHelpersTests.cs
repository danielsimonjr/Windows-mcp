using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Startup;
using Xunit;

namespace WindowsMcp.Tests.Startup;

[Trait("Category", "Unit")]
public class StartupApprovalTests
{
    [Theory]
    [InlineData(2, true)]   // 0x02 enabled
    [InlineData(6, true)]   // 0x06 enabled
    [InlineData(3, false)]  // 0x03 disabled
    [InlineData(7, false)]  // 0x07 disabled
    public void IsEnabled_decodes_first_byte_parity(byte first, bool expected)
    {
        StartupApproval.IsEnabled(new byte[] { first, 0, 0, 0 }).Should().Be(expected);
    }

    [Fact]
    public void IsEnabled_treats_absent_or_empty_flag_as_enabled()
    {
        StartupApproval.IsEnabled(null).Should().BeTrue();
        StartupApproval.IsEnabled(Array.Empty<byte>()).Should().BeTrue();
    }
}

[Trait("Category", "Unit")]
public class CommandTargetTests
{
    [Fact]
    public void ResolveExe_handles_quoted_executable()
    {
        CommandTarget.ResolveExe("\"C:\\Program Files\\App\\foo.exe\" --arg")
            .Should().Be("C:\\Program Files\\App\\foo.exe");
    }

    [Fact]
    public void ResolveExe_handles_unquoted_path_with_spaces_via_exe_suffix()
    {
        CommandTarget.ResolveExe("C:\\Program Files\\Adobe\\Creative Cloud.exe --showwindow=false")
            .Should().Be("C:\\Program Files\\Adobe\\Creative Cloud.exe");
    }

    [Fact]
    public void ResolveExe_handles_bare_token()
    {
        CommandTarget.ResolveExe("MessengerHelper.exe --lassie").Should().Be("MessengerHelper.exe");
    }

    [Fact]
    public void ResolveExe_returns_null_for_blank()
    {
        CommandTarget.ResolveExe(null).Should().BeNull();
        CommandTarget.ResolveExe("   ").Should().BeNull();
    }

    [Fact]
    public void Exists_is_true_for_real_system_binary_and_false_for_missing()
    {
        CommandTarget.Exists($"\"{Path.Combine(Environment.SystemDirectory, "kernel32.dll")}\"").Should().BeTrue();
        CommandTarget.Exists("C:\\nope\\definitely_missing_wmcp.exe --x").Should().BeFalse();
    }

    [Fact]
    public void Exists_resolves_bare_exe_via_PATH_not_just_system32()
    {
        // powershell.exe is on PATH (System32\WindowsPowerShell\v1.0) but NOT directly in
        // System32 — the case that made ResumeClaudeCode report a missing target.
        CommandTarget.Exists("powershell.exe").Should().BeTrue();
        CommandTarget.Exists("definitely_missing_wmcp_bare.exe").Should().BeFalse();
    }

    [Fact]
    public void ResolveFullPath_turns_a_bare_name_into_an_absolute_existing_path()
    {
        // Needed so a signature check on a bare command (e.g. Winlogon Shell = "explorer.exe")
        // can locate the real file instead of failing on a relative path.
        var p = CommandTarget.ResolveFullPath("explorer.exe");

        p.Should().NotBeNull();
        Path.IsPathRooted(p!).Should().BeTrue();
        File.Exists(p).Should().BeTrue();
        CommandTarget.ResolveFullPath("definitely_missing_wmcp_zzz.exe").Should().BeNull();
    }
}

[Trait("Category", "Unit")]
public class StartupReportRendererTests
{
    [Fact]
    public void Render_includes_header_section_titles_and_entries()
    {
        var dto = ReportFixtures.Empty(
            processes: new[] { new ProcessEntry(123, "foo", "C:\\foo.exe", 10, true, null) },
            run: new[] { new RunEntry("HKCU", "Software\\...\\Run", "Zoom", "zoom.exe", false, true, false, null) },
            errors: new[] { "lsp: boom" });

        var text = StartupReportRenderer.Render(dto);

        text.Should().Contain("Windows-mcp Startup Report");
        text.Should().Contain("Elevated: True");
        text.Should().Contain("Boot: Normal");                       // enriched header
        text.Should().Contain("== Processes (1) ==");
        text.Should().Contain("Zoom = zoom.exe");
        text.Should().Contain("enabled=N");
        text.Should().Contain("== Image File Execution Options (0) =="); // a new section renders
        text.Should().Contain("== Errors (1) ==").And.Contain("lsp: boom");
    }

    [Fact]
    public void RenderSummary_lists_only_flagged_entries_plus_counts()
    {
        var dto = ReportFixtures.Empty(run: new[]
        {
            new RunEntry("HKCU", "...\\Run", "Trusted", "good.exe", true, true, true, "CN=MS"),  // not flagged
            new RunEntry("HKLM", "...\\Run", "Sketchy", "bad.exe", true, true, false, null),     // untrusted -> flagged
        });

        var text = StartupReportRenderer.RenderSummary(dto);

        text.Should().Contain("SUMMARY");
        text.Should().Contain("== Section counts ==").And.Contain("run=2");
        text.Should().Contain("== Flagged MEDIUM untrusted-third-party (1) ==");
        text.Should().Contain("Sketchy").And.Contain("untrusted-third-party");
        text.Should().NotContain("== Flagged: untrusted or missing target (1) ==");
        text.Should().NotContain("UNTRUSTED");
        text.Should().NotContain("Trusted = good.exe");   // trusted entries are omitted from the summary
    }

    [Fact]
    public void RenderSummary_does_not_flag_tasks_without_an_exec_action()
    {
        // COM-handler tasks (null ActionPath) have no executable to verify — they must not
        // pollute the flagged list even though they carry no signer.
        var dto = ReportFixtures.Empty(tasks: new[]
        {
            new StartupTaskEntry("\\MS\\ComHandlerTask", "Ready", null, null, new[] { "Logon" }, true, false, null),
        });

        var text = StartupReportRenderer.RenderSummary(dto);

        text.Should().Contain("== Flagged HIGH missing-target / persistence hooks (0) ==");
        text.Should().Contain("== Flagged MEDIUM untrusted-third-party (0) ==");
        text.Should().Contain("== Flagged LOW ms-file-missing (0) ==");
        text.Should().NotContain("== Flagged: untrusted or missing target (0) ==");
        text.Should().NotContain("ComHandlerTask");
    }
}
