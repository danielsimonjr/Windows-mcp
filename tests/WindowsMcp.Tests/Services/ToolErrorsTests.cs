using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FluentAssertions;
using WindowsMcp;
using WindowsMcp.Abstractions;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// The MCP SDK masks every non-McpException as "An error occurred invoking '&lt;tool&gt;'.".
/// That hid the PID-reuse guard's abort behind the same text as a crash — so a caller could not
/// tell "I just saved you from killing an innocent process" from "the tool broke", and might
/// retry the kill without the guard. The CallTool filter surfaces caller-facing refusals; this
/// pins down which exceptions qualify, and — just as importantly — which must stay masked.
/// </summary>
[Trait("Category", "Unit")]
public class ToolErrorsTests
{
    [Fact]
    public void Deliberate_refusals_and_bad_input_are_caller_facing()
    {
        // Tools throw these to refuse: missing confirm, bad param combos, unknown action.
        ToolErrors.IsCallerFacing(new ArgumentException("'confirm: true' is required for kill"))
            .Should().BeTrue();

        // The PID-reuse start-time guard aborts with this. Its message IS the point.
        ToolErrors.IsCallerFacing(new InvalidOperationException(
            "pid 30872 start time … != expected …; aborting (possible PID reuse)"))
            .Should().BeTrue();

        // Process.GetProcessById on a dead PID — also actionable for the caller.
        ToolErrors.IsCallerFacing(new ArgumentException("Process with an Id of 999999 is not running."))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    public void Unexpected_faults_stay_masked(Type faultType)
    {
        // These are OUR bugs, not the caller's. Surfacing them would leak internals for no benefit
        // — the SDK's generic message is the right answer, so the filter must not claim them.
        // C-1 R4-7 adds the last two: a COM HRESULT or a Win32 error code is an internal fault
        // whatever it says, and widening the filter to IOException must not drag them in.
        var ex = (Exception)Activator.CreateInstance(faultType)!;
        ToolErrors.IsCallerFacing(ex).Should().BeFalse();
    }

    // ---- C-1 R4-7: the four types whose messages ARE the answer -------------------------------

    /// <summary>
    /// R4-7: these are the deliberate answers of registry_get ("Registry path not found: …"), the
    /// window matcher ("No top-level window matching 'x'. Open windows: …"), element ids, the app
    /// catalog, scheduled_task, watch, wait_for and every file tool. Masked as "An error occurred
    /// invoking '&lt;tool&gt;'", each of them turns a precise, actionable refusal into a crash
    /// report — and a caller that cannot read "no such window" retries the same call.
    /// </summary>
    [Theory]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(PathTooLongException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(TimeoutException))]
    public void Deliberate_answers_that_arrive_as_system_exceptions_are_caller_facing(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type)!;

        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    [Fact]
    public void The_messages_the_widening_exists_for_reach_the_caller()
    {
        // registry_get on a key that is not there.
        ToolErrors.IsCallerFacing(new KeyNotFoundException(
                @"Registry path not found: HKCU\Software\WindowsMcpTests\does-not-exist"))
            .Should().BeTrue();

        // The window matcher listing what IS open is the caller's next move.
        ToolErrors.IsCallerFacing(new KeyNotFoundException(
                "No top-level window matching 'nope'. Open windows: Notepad, Explorer."))
            .Should().BeTrue();

        // file_write(create_parents:false) naming the flag that would have created the directory.
        ToolErrors.IsCallerFacing(new DirectoryNotFoundException(
                @"Directory 'C:\tmp\nope' does not exist; pass create_parents:true to create it"))
            .Should().BeTrue();

        // A file held open by another process: "in use" is something the caller can act on.
        ToolErrors.IsCallerFacing(new IOException(
                "The process cannot access the file because it is being used by another process."))
            .Should().BeTrue();
    }

    // ---- C-1 R4b-6: the message the client actually receives ----------------------------------

    /// <summary>
    /// R4b-6: capping is the last step, not a rewrite. Everything a caller could read has to
    /// survive word for word — 2 000 characters is a screenful and far more than any deliberate
    /// refusal in this server needs.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(2000)]
    public void MessageFor_passes_a_message_within_the_cap_through_unchanged(int length)
    {
        var message = new string('m', length);

        ToolErrors.MessageFor(new InvalidOperationException(message)).Should().Be(message);
    }

    [Fact]
    public void MessageFor_keeps_a_real_refusal_word_for_word()
    {
        const string refusal = @"'C:\Windows' is a volume root and cannot be deleted";

        ToolErrors.MessageFor(new InvalidOperationException(refusal)).Should().Be(refusal);
    }

    /// <summary>
    /// R4b-6: a refusal quotes what the caller sent, and what the caller sent can be enormous — a
    /// registry path they pasted, a path a link cycle grew to 32 KB. Sending it all back is not an
    /// answer: it is the same thing again, at a size that costs the caller their context window.
    /// The head of the message says what happened; a marker says the rest was cut.
    /// </summary>
    [Theory]
    [InlineData(2001)]
    [InlineData(32_000)]
    public void MessageFor_caps_a_long_message_and_marks_it_as_cut(int length)
    {
        var message = @"Registry path not found: HKCU\" + new string('k', length);

        var capped = ToolErrors.MessageFor(new KeyNotFoundException(message));

        capped.Length.Should().BeLessThanOrEqualTo(2000, "no answer to a client is 32 KB of one path");
        capped.Should().StartWith(@"Registry path not found: HKCU\",
            "the head of the message is the part that says what happened");
        (capped.EndsWith('…') || capped.EndsWith("...", StringComparison.Ordinal)).Should().BeTrue(
            "a message cut without saying so reads as the whole answer - the caller acts on a path that is not the one they sent");
    }

    // ---- C-1 R4c-9: the cut is a cut, not a corruption ----------------------------------------

    /// <summary>
    /// R4c-9: the cut lands wherever 2 000 characters land, and what is there is the caller's own
    /// text — a file name, a registry value, a path they pasted. An emoji is ONE character and TWO
    /// <see cref="char"/>s (a surrogate pair), so a cut by index can land between the halves and
    /// leave a lone surrogate. That is not a shortened message: it is an invalid string, which the
    /// JSON writer either rejects or turns into U+FFFD — the caller loses the whole answer to a
    /// message the server chose to send them.
    /// <para>
    /// The head here is 1 972 ASCII characters, which puts the pair astride the cut for the
    /// 27-character marker in force today; the walk below does not depend on that arithmetic.
    /// </para>
    /// </summary>
    [Fact]
    public void MessageFor_never_cuts_a_surrogate_pair_in_half()
    {
        var head = new string('a', 1972);
        var message = head + string.Concat(Enumerable.Repeat("\U0001F600", 514));   // 1972 + 1028 = 3000 chars
        message.Length.Should().Be(3000, "the fixture only means anything if it is over the cap");

        var capped = ToolErrors.MessageFor(new InvalidOperationException(message));

        capped.Length.Should().BeLessThanOrEqualTo(2000, "the cap still applies");
        for (var i = 0; i < capped.Length; i++)
        {
            if (char.IsHighSurrogate(capped[i]))
                (i + 1 < capped.Length && char.IsLowSurrogate(capped[i + 1])).Should().BeTrue(
                    $"the high surrogate at index {i} has to keep its low half - a lone one is not a character");
            else if (char.IsLowSurrogate(capped[i]))
                (i > 0 && char.IsHighSurrogate(capped[i - 1])).Should().BeTrue(
                    $"the low surrogate at index {i} has to keep its high half");
        }
        capped.Should().StartWith(head[..200], "the head of the message is still word for word");
    }

    /// <summary>
    /// R4c-9: "…" alone says something was cut but not what the rule is. A caller who is told the
    /// limit is 2 000 characters can page, hash or narrow what they asked for; one who is not
    /// re-sends the same call.
    /// </summary>
    [Fact]
    public void MessageFor_marks_the_cut_with_the_limit_it_applied()
    {
        var capped = ToolErrors.MessageFor(new InvalidOperationException(new string('m', 5000)));

        var tail = capped[^40..];
        tail.Should().ContainEquivalentOf("cut", "the marker says the message was cut");
        tail.Should().Contain("2000", "and what the limit that cut it is");
    }

    /// <summary>
    /// R4c-9, the case the fixed-length sibling above cannot reach. Where the cut lands depends on
    /// the marker's own length, which is an implementation detail: with today's 30-character
    /// marker the cut falls at index 1 970, so a message whose emoji start at 1 972 is severed in
    /// the ASCII head and the surrogate guard never runs — the test passes whether the guard is
    /// there or not. Sweeping the head length across the whole plausible range puts the pair
    /// astride the cut for SOME case whatever the marker becomes, so the guard is exercised by
    /// construction rather than by luck.
    /// </summary>
    [Theory]
    [InlineData(1950)]
    [InlineData(1960)]
    [InlineData(1965)]
    [InlineData(1966)]
    [InlineData(1967)]
    [InlineData(1968)]
    [InlineData(1969)]
    [InlineData(1970)]
    [InlineData(1971)]
    [InlineData(1972)]
    [InlineData(1973)]
    [InlineData(1974)]
    [InlineData(1975)]
    [InlineData(1980)]
    [InlineData(1990)]
    [InlineData(1999)]
    public void MessageFor_never_cuts_a_surrogate_pair_wherever_the_cut_lands(int headLength)
    {
        var head = new string('a', headLength);
        // 300 emoji: 600 chars, so the message is over the cap for every head length here and the
        // cut is guaranteed to land inside the run of surrogate pairs for at least one of them.
        var message = head + string.Concat(Enumerable.Repeat("\U0001F600", 300));

        var capped = ToolErrors.MessageFor(new InvalidOperationException(message));

        capped.Length.Should().BeLessThanOrEqualTo(2000, "the cap still applies");
        for (var i = 0; i < capped.Length; i++)
        {
            if (char.IsHighSurrogate(capped[i]))
                (i + 1 < capped.Length && char.IsLowSurrogate(capped[i + 1])).Should().BeTrue(
                    $"the high surrogate at index {i} has to keep its low half - a lone one is not a character, "
                    + $"and the JSON writer turns it into U+FFFD or refuses the whole response (head {headLength})");
            else if (char.IsLowSurrogate(capped[i]))
                (i > 0 && char.IsHighSurrogate(capped[i - 1])).Should().BeTrue(
                    $"the low surrogate at index {i} has to keep its high half (head {headLength})");
        }
        capped.Should().StartWith(head[..200], "the head of the message is still word for word");
        capped.Should().ContainEquivalentOf("cut", "and the marker still says the message was cut");
    }

    // ---- D-10 (R3): the family guard ----------------------------------------------------------

    /// <summary>
    /// One reasoned exemption: <paramref name="Site"/> constructing <paramref name="Exception"/>
    /// on purpose, and why it must stay masked. <paramref name="Site"/> is a type's full name
    /// (covering its methods and its nested lambda/state-machine types) or one method
    /// (<c>Namespace.Type.Method</c>).
    /// </summary>
    private sealed record Exemption(string Site, Type Exception, string Reason)
    {
        public bool Covers(string site) =>
            site == Site
            || site.StartsWith(Site + ".", StringComparison.Ordinal)
            || site.StartsWith(Site + "+", StringComparison.Ordinal);
    }

    /// <summary>
    /// D-10 R16a: the constructions our own code makes on purpose that MUST stay masked, keyed by
    /// (site, exception type). Anything else the server assembly constructs has to be
    /// caller-facing. Keyed by SITE, not by type alone: a type-wide exemption for
    /// <c>Win32Exception</c> let <c>PowerService.ExecuteAsync</c> throw the OS's refusals as a
    /// masked fault (R15) — the token plumbing and the refusals live in the same type, so even a
    /// declaring-type key cannot tell them apart. Adding an entry is a decision a reviewer sees in
    /// the diff; constructing a new one silently is what D-10 was.
    /// </summary>
    private static readonly Exemption[] MaskedOnPurpose =
    [
        new("WindowsMcp.Services.Win32PowerNative.EnableShutdownPrivilege", typeof(System.ComponentModel.Win32Exception),
            "the SeShutdownPrivilege token plumbing (OpenProcessToken, LookupPrivilegeValue, AdjustTokenPrivileges) failing " +
            "is a fault, not a refusal; the OS refusing the power action itself is an InvalidOperationException (R15)"),
        new("WindowsMcp.Hosting.ServerOptions", typeof(WindowsMcp.Hosting.OptionsException),
            "a command-line parse failure, reported by Program before any tool exists - never on a tool path"),
        new("WindowsMcp.Hosting.HotkeyParser", typeof(WindowsMcp.Hosting.OptionsException),
            "a --hotkey option parse failure, reported by Program before any tool exists - never on a tool path"),
    ];

    /// <summary>
    /// D-10 R16b: the sites allowed to call <c>Marshal.ThrowExceptionForHR</c> /
    /// <c>Marshal.GetExceptionForHR</c> — our own code throwing a COMException with no
    /// <c>newobj</c> for the scan above to see.
    /// </summary>
    private static readonly Exemption[] HResultThrowsOnPurpose =
    [
        new("WindowsMcp.Services.WgcCaptureBackend", typeof(System.Runtime.InteropServices.COMException),
            "a DXGI QueryInterface, D3D11 device creation or WindowsCreateString (CsWin32 HRESULT.ThrowOnFailure) " +
            "failing mid-capture is a graphics-stack fault, not a refusal the caller could act on"),
    ];

    /// <summary>
    /// D-10 (R3): the strongest guard that works in a clean clone without a source tree — every
    /// <c>newobj</c> of an exception type in the IL of <c>WindowsMcp</c> and
    /// <c>WindowsMcp.Abstractions</c>, lambdas, closures and async/iterator state machines
    /// included, must construct a type <see cref="ToolErrors.IsCallerFacing"/> accepts, or one on
    /// the reasoned <see cref="MaskedOnPurpose"/> list. It reads the built assemblies, not
    /// <c>src/</c>, so formatting, aliases and helper factories (D-10's
    /// <c>UiRefusals.PatternNotSupported</c>) cannot hide a construction.
    /// <para>
    /// What it cannot catch: an exception thrown by a library or the framework on our behalf
    /// (<c>int.Parse</c>'s <c>FormatException</c>, FlaUI's <c>PropertyNotSupportedException</c>,
    /// HttpClient's <c>NotSupportedException</c>) — those need the call site to validate first, as
    /// WebService's scheme check does; a caller-facing type thrown for what is really a fault
    /// (that is over-surfacing, a review question); and whether a <c>MaskedOnPurpose</c> type is
    /// ever used for a deliberate refusal.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_exception_the_server_constructs_is_caller_facing_or_masked_on_purpose()
    {
        var offenders = ConstructedExceptions(typeof(ToolErrors).Assembly, typeof(IInputService).Assembly)
            .Where(c => !ToolErrors.IsCallerFacing((Exception)RuntimeHelpers.GetUninitializedObject(c.Exception)))
            .Where(c => !MaskedOnPurpose.Any(e => e.Exception == c.Exception && e.Covers(c.Site)))
            .Select(c => $"{c.Site} constructs {c.Exception.FullName}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Joined, so the failure lists every offender rather than a truncated collection.
        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "a deliberate refusal thrown as a type ToolErrors.IsCallerFacing rejects reaches the caller as " +
            "\"An error occurred invoking '<tool>'.\" - use ArgumentException / InvalidOperationException / " +
            "KeyNotFoundException / IOException / UnauthorizedAccessException / TimeoutException, or add the type " +
            "to MaskedOnPurpose with the reason it is a fault and not a refusal");
    }

    /// <summary>
    /// D-10 R16a: an exemption is only as good as the site it names. One that names a site which
    /// no longer constructs that type is dead weight that would silently re-admit whatever lands
    /// there next — and one that names a site which does not exist yet (Win32PowerNative, until
    /// R15 lands) is a decision that has not been carried out.
    /// </summary>
    [Fact]
    public void Every_masked_on_purpose_exemption_names_a_construction_that_exists()
    {
        var found = ConstructedExceptions(typeof(ToolErrors).Assembly, typeof(IInputService).Assembly).ToList();

        var stale = MaskedOnPurpose
            .Where(e => !found.Any(c => c.Exception == e.Exception && e.Covers(c.Site)))
            .Select(e => $"{e.Site} / {e.Exception.Name}")
            .ToList();

        string.Join(Environment.NewLine, stale).Should().BeEmpty(
            "an exemption for a construction that is not there is either stale (delete it) or not yet implemented");
    }

    [Theory]
    [InlineData("WindowsMcp.Services.Win32PowerNative.EnableShutdownPrivilege", true)]
    [InlineData("WindowsMcp.Services.Win32PowerNative.EnableShutdownPrivilegeEx", false)]   // a prefix is not the method
    [InlineData("WindowsMcp.Services.Win32PowerNative.ExitWindows", false)]                 // same type, another method
    [InlineData("WindowsMcp.Services.PowerService.ExecuteAsync", false)]                     // the R15 refusals
    [InlineData("WindowsMcp.Services.PowerService+<>c.<ExecuteAsync>b__0_0", false)]
    public void A_Win32Exception_exemption_covers_its_one_site_and_nothing_else(string site, bool covered)
    {
        MaskedOnPurpose.Any(e => e.Exception == typeof(System.ComponentModel.Win32Exception) && e.Covers(site))
            .Should().Be(covered);
    }

    [Theory]
    [InlineData("WindowsMcp.Hosting.ServerOptions.Parse", true)]
    [InlineData("WindowsMcp.Hosting.ServerOptions+<>c.<Parse>b__1_0", true)]   // a lambda inside the type
    [InlineData("WindowsMcp.Hosting.ServerOptionsX.Parse", false)]             // a prefix is not the type
    [InlineData("WindowsMcp.Tools.SystemTools.Power", false)]
    public void A_type_wide_exemption_covers_the_type_and_its_nested_types_only(string site, bool covered)
    {
        MaskedOnPurpose.Any(e => e.Exception == typeof(WindowsMcp.Hosting.OptionsException) && e.Covers(site))
            .Should().Be(covered);
    }

    // ---- D-10 R16b: our own code throwing an HRESULT with no newobj ---------------------------

    /// <summary>
    /// <c>Marshal.ThrowExceptionForHR(hr)</c> throws a COMException (or whatever the HRESULT maps
    /// to) with no <c>newobj</c> in our IL, so the construction scan cannot see it.
    /// <c>Win32AppActivator.ActivatePackaged</c> did exactly that for a launch the caller has to
    /// hear about (R12). Every such call must be on the reasoned
    /// <see cref="HResultThrowsOnPurpose"/> list.
    /// </summary>
    [Fact]
    public void Every_HRESULT_throw_the_server_makes_is_on_the_reasoned_list()
    {
        var offenders = HResultThrows(typeof(ToolErrors).Assembly, typeof(IInputService).Assembly)
            .Where(site => !HResultThrowsOnPurpose.Any(e => e.Covers(site.Site)))
            .Select(site => $"{site.Site} calls {site.Method}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "an HRESULT thrown as a COMException reaches the caller as \"An error occurred invoking '<tool>'.\" - " +
            "throw a caller-facing exception carrying the HRESULT in hex, or add the site to HResultThrowsOnPurpose with the reason");
    }

    [Fact]
    public void The_HRESULT_scan_sees_the_capture_backends_call()
    {
        // Non-vacuity: a scan that finds nothing passes for ever.
        HResultThrows(typeof(ToolErrors).Assembly)
            .Should().Contain(s => s.Site.StartsWith("WindowsMcp.Services.WgcCaptureBackend", StringComparison.Ordinal)
                && s.Method == "Marshal.ThrowExceptionForHR");
    }

    /// <summary>
    /// D-10 R27: CsWin32's <c>Windows.Win32.Foundation.HRESULT.ThrowOnFailure()</c> calls
    /// <c>Marshal.ThrowExceptionForHR</c> from INSIDE the generated <c>Windows.Win32</c> namespace,
    /// which <see cref="OwnMethodBodies"/> skips — so a call to it from our code threw a COMException
    /// the scan never saw. <c>WgcCaptureBackend</c> makes three (D3D11CreateDevice,
    /// CreateDirect3D11DeviceFromDXGIDevice, WindowsCreateString); the scan must see them, which is
    /// what puts every future one through the reasoned list.
    /// </summary>
    [Fact]
    public void The_HRESULT_scan_sees_CsWin32s_ThrowOnFailure_at_the_capture_backends_call_sites()
    {
        var sites = HResultThrows(typeof(ToolErrors).Assembly)
            .Where(s => s.Method == "HRESULT.ThrowOnFailure")
            .Select(s => s.Site)
            .Distinct()
            .ToList();

        sites.Should().NotBeEmpty("a ThrowOnFailure call is an HRESULT throw with no newobj in our IL");
        sites.Should().OnlyContain(s => s.StartsWith("WindowsMcp.Services.WgcCaptureBackend", StringComparison.Ordinal),
            "WgcCaptureBackend is the only reasoned site today; anything else must fail the guard above");
        sites.Count.Should().BeGreaterThanOrEqualTo(2,
            "device creation and the HSTRING helper are different methods; a scan that sees one of them is half a scan");
    }

    /// <summary>
    /// Keeps the guard above honest: an IL walk that silently found nothing would pass for ever.
    /// These are constructions the guard must see — a lambda body on the UIA service, an async
    /// tool method's state machine, and a plain method.
    /// </summary>
    [Fact]
    public void The_family_guard_sees_constructions_in_lambdas_state_machines_and_plain_methods()
    {
        var found = ConstructedExceptions(typeof(ToolErrors).Assembly).ToList();

        // R16c: a construction that exists ONLY inside a closure's display class. Since D-11 no
        // KeyNotFoundException is constructed in a UIAutomationService lambda (lookups moved to
        // ElementCache.Get), so the old "KeyNotFoundException anywhere in UIAutomationService"
        // check was satisfied by plain methods and iterators and no longer proved lambdas are seen.
        // Round 2: GetTableAsync's GridPattern refusal moved into the named MissingPattern helper,
        // so the pin moved to the one construction still written inline in a capturing lambda:
        // SnapshotAsync's "no automation element for the window handle" inside its OnStaAsync body
        // (the lambda captures this, request and the targets, so it lives in a display class).
        found.Should().Contain(c => c.Exception == typeof(InvalidOperationException)
                && c.Site.StartsWith("WindowsMcp.Services.UIAutomationService+<>c", StringComparison.Ordinal)
                && c.Site.Contains("<SnapshotAsync>", StringComparison.Ordinal),
            "SnapshotAsync's \"no automation element for the window handle\" is built inside its OnStaAsync lambda's display class");
        found.Should().Contain(c => c.Exception == typeof(ArgumentException)
                && c.Site.StartsWith("WindowsMcp.Tools.InputTools+<", StringComparison.Ordinal),
            "InputTools.Drag is async: its argument checks live in a compiler-generated state machine");
        found.Should().Contain(c => c.Exception == typeof(ArgumentOutOfRangeException)
                && c.Site.StartsWith("WindowsMcp.Services.UiTree.ElementBudget", StringComparison.Ordinal),
            "and a plain constructor's guard");
        found.Select(c => c.Exception).Distinct().Count().Should().BeGreaterThan(5,
            "the server constructs a dozen exception types; a walk that sees two is broken");
    }

    [Fact]
    public void The_family_guard_ignores_the_compiler_generated_iterator_Reset()
    {
        // Every C# iterator's synthesized IEnumerator.Reset throws NotSupportedException. It is not
        // a refusal and no tool can reach it; flagging it would make the guard unusable.
        ConstructedExceptions(typeof(ToolErrors).Assembly)
            .Should().NotContain(c => c.Site.EndsWith("IEnumerator.Reset", StringComparison.Ordinal));
    }

    private sealed record Construction(string Site, Type Exception);

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>Every <c>newobj</c> of an <see cref="Exception"/> subtype in the given assemblies' own IL.</summary>
    private static IEnumerable<Construction> ConstructedExceptions(params Assembly[] assemblies)
    {
        foreach (var (type, method, il, site) in OwnMethodBodies(assemblies))
            foreach (var token in MethodTokens(il, OpCodes.Newobj))
            {
                var ctor = method.Module.ResolveMethod(token,
                    type.IsGenericType ? type.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                var created = ctor?.DeclaringType;
                if (created is not null && typeof(Exception).IsAssignableFrom(created))
                    yield return new Construction(site, created);
            }
    }

    private sealed record HResultThrow(string Site, string Method);

    private static readonly string[] HResultThrowers = ["ThrowExceptionForHR", "GetExceptionForHR"];

    /// <summary>
    /// Every <c>call</c> to <c>Marshal.ThrowExceptionForHR</c> / <c>Marshal.GetExceptionForHR</c> (any
    /// overload), and (R27) to CsWin32's <c>Windows.Win32.Foundation.HRESULT.ThrowOnFailure</c>, which
    /// is generated into our own assembly and throws through Marshal where the scan does not look.
    /// <c>Method</c> is "Marshal.&lt;name&gt;" or "HRESULT.ThrowOnFailure".
    /// </summary>
    private static IEnumerable<HResultThrow> HResultThrows(params Assembly[] assemblies)
    {
        foreach (var (type, method, il, site) in OwnMethodBodies(assemblies))
            foreach (var token in MethodTokens(il, OpCodes.Call, OpCodes.Callvirt))
            {
                MethodBase? callee;
                try
                {
                    callee = method.Module.ResolveMethod(token,
                        type.IsGenericType ? type.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                // A call into an open generic the context cannot close. Marshal's HRESULT methods
                // are non-generic, so a failure here can never hide one of them.
                catch (ArgumentException) { continue; }
                if (callee?.DeclaringType == typeof(System.Runtime.InteropServices.Marshal)
                    && HResultThrowers.Contains(callee.Name))
                    yield return new HResultThrow(site, "Marshal." + callee.Name);
                else if (callee?.DeclaringType?.FullName == "Windows.Win32.Foundation.HRESULT"
                    && callee.Name == "ThrowOnFailure")
                    yield return new HResultThrow(site, "HRESULT.ThrowOnFailure");
            }
    }

    /// <summary>
    /// Every method body in the given assemblies that is ours to answer for: generated interop,
    /// regex runners, collection-expression wrappers and iterator <c>Reset</c> are skipped.
    /// </summary>
    private static IEnumerable<(Type Type, MethodBase Method, byte[] Il, string Site)> OwnMethodBodies(params Assembly[] assemblies)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var assembly in assemblies)
            foreach (var type in assembly.GetTypes())
            {
                // Source-generated interop (CsWin32) and [GeneratedRegex] runners are not our refusal paths.
                if (type.Namespace is { } ns
                    && (ns.StartsWith("Windows.Win32", StringComparison.Ordinal)
                        || ns.StartsWith("System.Text.RegularExpressions.Generated", StringComparison.Ordinal)))
                    continue;

                // The compiler's synthesized collection-expression wrappers (<>z__ReadOnlyArray<T> and
                // friends): their IList/ICollection mutators throw NotSupportedException by contract.
                if (type.Name.StartsWith("<>z__", StringComparison.Ordinal)) continue;

                bool generated = type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
                var methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
                foreach (var method in methods)
                {
                    if (generated && method.Name.EndsWith("IEnumerator.Reset", StringComparison.Ordinal)) continue;
                    var il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    yield return (type, method, il, $"{type.FullName}.{method.Name}");
                }
            }
    }

    /// <summary>The metadata tokens of every instruction in a method body whose opcode is one of <paramref name="wanted"/>.</summary>
    private static List<int> MethodTokens(byte[] il, params OpCode[] wanted)
    {
        var tokens = new List<int>();
        int i = 0;
        while (i < il.Length)
        {
            OpCode op = il[i] == 0xFE
                ? OpCodesByValue[unchecked((short)(0xFE00 | il[i + 1]))]
                : OpCodesByValue[il[i]];
            i += op.Size;

            if (Array.IndexOf(wanted, op) >= 0) tokens.Add(BitConverter.ToInt32(il, i));

            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,   // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString, InlineTok, InlineType, ShortInlineR
            };
        }
        return tokens;
    }
}
