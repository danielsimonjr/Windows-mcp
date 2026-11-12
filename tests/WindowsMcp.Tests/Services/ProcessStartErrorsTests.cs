using System.ComponentModel;
using System.Runtime.InteropServices;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-10 R12: a missing executable is a refusal the caller has to read. <c>start_process</c>
/// (command:"notepadd") and <c>launch</c> (a path or a stale shortcut) both reach
/// <c>Process.Start</c>, which throws <see cref="Win32Exception"/> ("The system cannot find the
/// file specified"); packaged activation throws a <see cref="COMException"/>. Neither is
/// caller-facing, so both read "An error occurred invoking '&lt;tool&gt;'.". The pure translation is
/// here; <see cref="ProcessStartErrorsIntegrationTests"/> drives the real start paths.
/// </summary>
[Trait("Category", "Unit")]
public class ProcessStartErrorsTests
{
    private const string What = "notepadd";

    [Theory]
    [InlineData(2)]   // ERROR_FILE_NOT_FOUND
    [InlineData(3)]   // ERROR_PATH_NOT_FOUND
    public void A_missing_file_or_path_is_a_FileNotFoundException_naming_what_was_started(int error)
    {
        var translated = ProcessStartErrors.Translate(new Win32Exception(error), What);

        translated.Should().BeOfType<FileNotFoundException>();
        ToolErrors.IsCallerFacing(translated!).Should().BeTrue();
        translated!.Message.Should().Contain($"'{What}'", "the caller is told which name did not resolve");
    }

    [Fact]
    public void Access_denied_is_an_UnauthorizedAccessException_naming_what_was_started()
    {
        var translated = ProcessStartErrors.Translate(new Win32Exception(5), What);

        translated.Should().BeOfType<UnauthorizedAccessException>();
        translated!.Message.Should().Contain($"'{What}'");
    }

    [Theory]
    [InlineData(193)]    // ERROR_BAD_EXE_FORMAT
    [InlineData(1155)]   // ERROR_NO_ASSOCIATION
    [InlineData(740)]    // ERROR_ELEVATION_REQUIRED
    public void Any_other_start_error_is_an_InvalidOperationException_carrying_the_OS_reason(int error)
    {
        var os = new Win32Exception(error);

        var translated = ProcessStartErrors.Translate(os, What);

        translated.Should().BeOfType<InvalidOperationException>();
        translated!.Message.Should().StartWith($"Could not start '{What}': ");
        translated.Message.Should().Contain(os.Message, "the OS says why; the caller cannot act without it");
    }

    [Theory]
    [InlineData(0x80070002u)]
    [InlineData(0x80270254u)]
    [InlineData(0x80004005u)]
    public void A_COM_activation_failure_is_an_InvalidOperationException_with_the_HRESULT_in_hex(uint hr)
    {
        var translated = ProcessStartErrors.Translate(new COMException("activation failed", unchecked((int)hr)), What);

        translated.Should().BeOfType<InvalidOperationException>();
        translated!.Message.Should().Contain(What);
        translated.Message.Should().ContainEquivalentOf($"0x{hr:X8}", "an HRESULT is only searchable in hex");
    }

    [Fact]
    public void The_translation_keeps_the_original_as_the_inner_exception()
    {
        var os = new Win32Exception(2);

        ProcessStartErrors.Translate(os, What)!.InnerException.Should().BeSameAs(os);
    }

    // ---- R26: the not-found hint ----------------------------------------------------------------

    private const string Hint = "Quote the path.";

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_not_found_hint_replaces_the_default_advice(int error)
    {
        var message = ProcessStartErrors.Translate(new Win32Exception(error), What, Hint)!.Message;

        message.Should().StartWith($"Could not start '{What}': the file or command was not found. ");
        message.Should().EndWith(Hint);
        message.Should().NotContain("give a full path", "the caller-specific hint replaces the generic one");
    }

    [Fact]
    public void Without_a_hint_a_not_found_says_to_check_the_name_or_give_a_full_path()
        => ProcessStartErrors.Translate(new Win32Exception(2), What)!.Message.Should().Contain("give a full path");

    [Theory]
    [InlineData(5)]
    [InlineData(193)]
    public void A_not_found_hint_is_not_added_to_any_other_failure(int error)
        => ProcessStartErrors.Translate(new Win32Exception(error), What, Hint)!.Message.Should().NotContain(Hint);

    [Fact]
    public void A_not_found_hint_is_not_added_to_an_activation_failure()
        => ProcessStartErrors.Translate(new COMException("x", unchecked((int)0x80070002)), What, Hint)!
            .Message.Should().NotContain(Hint);

    public static TheoryData<Exception> NotAStartFailure => new()
    {
        new NullReferenceException(),
        new InvalidOperationException("already caller-facing"),
        new ArgumentException("bad", "command"),
        new DirectoryNotFoundException("cwd 'x' is not an existing directory."),
        new OperationCanceledException(),
    };

    [Theory]
    [MemberData(nameof(NotAStartFailure))]
    public void Anything_else_is_left_alone(Exception ex)
        => ProcessStartErrors.Translate(ex, What).Should().BeNull();
}

/// <summary>
/// D-10 R12 through the real start paths. Starting a name that does not exist starts nothing, so
/// these are read-only: <c>Process.Start</c> (direct and ShellExecute, with the
/// <c>ErrorDialog:false</c> default so no UI appears), <c>Win32AppActivator</c>'s two routes, and
/// both <c>WindowService.LaunchAsync</c> overloads.
/// </summary>
[Trait("Category", "Integration")]
public class ProcessStartErrorsIntegrationTests
{
    private static string MissingCommand() => "wmcp-no-such-command-" + Guid.NewGuid().ToString("N");

    /// <summary>A path under a temp directory that does not exist either.</summary>
    private static string MissingShortcut() =>
        Path.Combine(Path.GetTempPath(), "wmcp-launch-" + Guid.NewGuid().ToString("N"), "nope.lnk");

    private static ProcessService Process() => new(new Mock<IWmiService>().Object);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_process_with_a_command_that_does_not_exist_is_a_FileNotFoundException_naming_it(bool shellExecute)
    {
        var missing = MissingCommand();

        Func<Task> act = () => Process().StartDetachedAsync(new ProcessStart(missing, null, null, shellExecute));

        var ex = (await act.Should().ThrowAsync<FileNotFoundException>()).Which;
        ex.Message.Should().Contain(missing);
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    [Fact]
    public async Task Start_process_in_argv_mode_with_a_missing_executable_is_a_FileNotFoundException_naming_it()
    {
        var missing = MissingCommand() + ".exe";

        Func<Task> act = () => Process().StartDetachedAsync(new ProcessStart(missing, ["a"], null, false));

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.Message.Should().Contain(missing);
    }

    // ---- D-10 R26 (round 3): name the file actually tried ------------------------------------
    //
    // Command-only mode splits at the first space (SplitCommand), so an unquoted path with a space
    // tries 'C:\Program'. Round 2 quoted the whole command line back and said "give a full path" —
    // which the caller already did. The message names psi.FileName, and adds the remedy that works.

    /// <summary>
    /// A path with a space that exists nowhere. The random part comes BEFORE the first space:
    /// command-only mode tries the text up to that space, and a fixed prefix such as
    /// <c>C:\Program</c> could be a real file or folder on some machine, which this test would
    /// then launch or open.
    /// </summary>
    private static string SpacedMissingPath() =>
        $@"C:\wmcp-nosuch-{Guid.NewGuid():N} dir\app.exe";

    /// <summary>What command-only mode tries: the text before the first space.</summary>
    private static string FirstToken(string path) => path[..path.IndexOf(' ')];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unquoted_spaced_command_names_the_file_actually_tried_and_says_to_quote_it_or_use_args_json(bool shellExecute)
    {
        var path = SpacedMissingPath();
        var command = path + " --flag";

        Func<Task> act = () => Process().StartDetachedAsync(new ProcessStart(command, null, null, shellExecute));

        var ex = (await act.Should().ThrowAsync<FileNotFoundException>()).Which;
        ex.Message.Should().Contain($"'{FirstToken(path)}'", "SplitCommand tried the text up to the first space");
        ex.Message.Should().NotContain(command, "the whole command line is not what was tried");
        ex.Message.Should().ContainEquivalentOf("quote", "the fix for a spaced path in one command line");
        ex.Message.Should().Contain("args_json", "or pass the executable and its arguments separately");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    [Fact]
    public async Task A_quoted_spaced_command_names_the_whole_unquoted_path()
    {
        var path = SpacedMissingPath();

        Func<Task> act = () => Process().StartDetachedAsync(new ProcessStart($"\"{path}\" --flag", null, null, false));

        var ex = (await act.Should().ThrowAsync<FileNotFoundException>()).Which;
        ex.Message.Should().Contain($"'{path}'", "the quoted executable is what was tried, without its quotes or arguments");
    }

    [Fact]
    public async Task Argv_mode_with_a_spaced_missing_path_names_the_whole_path_and_gives_no_quoting_hint()
    {
        // argv mode is unchanged: the command IS the executable, so there is nothing to split or quote.
        var path = SpacedMissingPath();

        Func<Task> act = () => Process().StartDetachedAsync(new ProcessStart(path, ["--flag"], null, false));

        var ex = (await act.Should().ThrowAsync<FileNotFoundException>()).Which;
        ex.Message.Should().Contain($"'{path}'");
        ex.Message.Should().NotContain("args_json", "args_json was already used");
    }

    [Fact]
    public async Task A_command_without_a_space_gets_no_quoting_hint()
    {
        var missing = MissingCommand();

        Func<Task> act = () => Process().StartDetachedAsync(new ProcessStart(missing, null, null, false));

        var ex = (await act.Should().ThrowAsync<FileNotFoundException>()).Which;
        ex.Message.Should().Contain($"'{missing}'");
        ex.Message.Should().NotContain("args_json", "the hint is for a command line that was split at a space");
    }

    [Fact]
    public void The_shell_route_on_a_missing_shortcut_is_a_FileNotFoundException_naming_it()
    {
        var missing = MissingShortcut();

        var act = () => Win32AppActivator.Instance.StartShortcutOrPath(missing);

        act.Should().Throw<FileNotFoundException>().Which.Message.Should().Contain(missing);
    }

    [Fact]
    public void Packaged_activation_of_an_unknown_app_is_an_InvalidOperationException_with_the_HRESULT()
    {
        var aumid = "WindowsMcp.Tests.NoSuchPackage_" + Guid.NewGuid().ToString("N")[..12] + "!App";

        var act = () => Win32AppActivator.Instance.ActivatePackaged(aumid);

        var ex = act.Should().Throw<InvalidOperationException>(
            "Marshal.ThrowExceptionForHR raised a COMException, which the SDK masks").Which;
        ex.Message.Should().Contain(aumid);
        ex.Message.Should().MatchRegex("0[xX][0-9A-Fa-f]{8}", "the HRESULT, in hex");
    }

    [Fact]
    public async Task The_legacy_launch_on_a_missing_path_is_a_FileNotFoundException_naming_it()
    {
        var missing = MissingShortcut();

        Func<Task> act = () => new WindowService().LaunchAsync(missing);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.Message.Should().Contain(missing);
    }

    [Fact]
    public async Task Launch_of_a_catalog_entry_whose_shortcut_is_gone_is_a_FileNotFoundException_naming_the_target()
    {
        // A Start Menu entry the catalog still lists after its .lnk was deleted: the catalog is a
        // mock (the decision is not under test), the activator is the real one.
        var missing = MissingShortcut();
        var catalog = new Mock<IAppCatalogService>();
        catalog.Setup(c => c.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppMatch(new AppEntry("Gone App", "shortcut", missing, Path.GetDirectoryName(missing)!), 100, "exact"));
        var svc = new WindowService(null, null, catalog.Object);

        Func<Task> act = () => svc.LaunchAsync("Gone App", waitForWindow: false, timeoutMs: 1000);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.Message.Should().Contain(missing);
    }
}
