using System.ComponentModel;
using System.Runtime.InteropServices;
using FluentAssertions;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// D-10 (R1): <c>interact_element</c>'s "pattern not supported" refusal. It used to be a
/// <c>NotSupportedException</c>, which <c>ToolErrors.IsCallerFacing</c> does not list, so the
/// call-tool filter let the SDK flatten it to "An error occurred invoking 'interact_element'." —
/// while the tool's own description promises "an unsupported pattern errors naming the control
/// type". An <c>AutomationElement</c> cannot be faked, so the refusal is built by a pure factory
/// and tested here; <c>UIAutomationServiceTests</c> holds the live sibling.
/// </summary>
[Trait("Category", "Unit")]
public class UiRefusalsTests
{
    [Theory]
    [InlineData("InvokePattern", "Pane", "Toolbar")]
    [InlineData("TogglePattern", "Button", "Save")]
    [InlineData("SelectionItemPattern", "Text", "Status")]
    public void PatternNotSupported_is_an_InvalidOperationException(string pattern, string controlType, string name)
    {
        var ex = UiRefusals.PatternNotSupported(pattern, controlType, name);

        ex.Should().BeOfType<InvalidOperationException>(
            "the D-10 decision: a deliberate refusal belongs to the caller-facing set");
    }

    [Theory]
    [InlineData("InvokePattern", "Pane", "Toolbar")]
    [InlineData("TogglePattern", "Button", "Save")]
    [InlineData("SelectionItemPattern", "Text", "Status")]
    public void PatternNotSupported_reaches_the_caller_through_the_call_tool_filter(string pattern, string controlType, string name)
    {
        var ex = UiRefusals.PatternNotSupported(pattern, controlType, name);

        ToolErrors.IsCallerFacing(ex).Should().BeTrue("a masked refusal is indistinguishable from a crash");
    }

    [Fact]
    public void PatternNotSupported_names_the_pattern_the_control_type_and_the_element_name()
    {
        var message = ToolErrors.MessageFor(UiRefusals.PatternNotSupported("TogglePattern", "Button", "Save"));

        message.Should().Contain("TogglePattern", "the caller has to know which verb was wrong");
        message.Should().Contain("Button", "the description promises the control type");
        message.Should().Contain("'Save'", "and the name tells the caller which element it was");
    }

    [Fact]
    public void PatternNotSupported_keeps_an_empty_name_visible()
    {
        // Many controls have no Name. The message must still read as a sentence about a control,
        // not collapse the quotes away so the caller cannot tell a name was missing.
        var message = UiRefusals.PatternNotSupported("InvokePattern", "Pane", "").Message;

        message.Should().Contain("InvokePattern").And.Contain("Pane").And.Contain("''");
    }

    // ---- D-10 R11: what the TARGET throws, translated ----------------------------------------
    //
    // interact_element on a disabled control: UIA answers UIA_E_ELEMENTNOTENABLED and FlaUI throws
    // ElementNotEnabledException; a provider without a property throws PropertyNotSupportedException;
    // an element destroyed since its id was issued throws ElementNotAvailableException. All of them
    // derive FlaUIException : Exception and none is caller-facing, so each reached the client as
    // "An error occurred invoking 'interact_element'.". The service catches around the STA work of
    // interact / get_table / get_text / focus and asks Translate;
    // UIAutomationServiceRefusalIntegrationTests is the live sibling.
    //
    // Round 3 (R20/R28): every input below is either a FlaUI exception type or what FlaUI's own
    // Com.Call turns an HRESULT into (FlaUiShaped). The round-2 rows that fed raw COMExceptions
    // (UIA_E_ELEMENTNOTENABLED, UIA_E_NOTSUPPORTED, UIA_E_ELEMENTNOTAVAILABLE, RPC_E_DISCONNECTED,
    // RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED) are deleted: no FlaUI UIA3 call delivers a raw
    // COMException, so they proved a branch production never takes. The RPC failures arrive as a
    // Win32Exception with the HRESULT lost; they are NOT translatable from the exception alone
    // (NotARefusal below) — only the liveness probe (ProbeSaysGone) can say the target died.

    private const string Id = "el_42";
    private const string ControlType = "Button";
    private const string ElementName = "Save";

    private static Exception Translated(Exception ex, string action = "invoke")
    {
        var translated = UiRefusals.Translate(ex, Id, action, ControlType, ElementName);
        translated.Should().NotBeNull($"{ex.GetType().Name} is a refusal the caller has to read, not a fault to mask");
        return translated!;
    }

    public static TheoryData<Exception> NotEnabled => new()
    {
        new FlaUI.Core.Exceptions.ElementNotEnabledException(),
        new FlaUI.Core.Exceptions.ElementNotEnabledException("UIA_E_ELEMENTNOTENABLED"),
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTENABLED),
    };

    [Theory]
    [MemberData(nameof(NotEnabled))]
    public void Translate_turns_element_not_enabled_into_a_caller_facing_refusal_saying_the_control_is_disabled(Exception ex)
    {
        var translated = Translated(ex, action: "toggle");

        translated.Should().BeOfType<InvalidOperationException>();
        ToolErrors.IsCallerFacing(translated).Should().BeTrue();
        translated.Message.Should().ContainEquivalentOf("disabled", "the caller has to learn WHY nothing happened");
        translated.Message.Should().Contain("toggle", "and which action was refused");
        translated.Message.Should().Contain(ControlType).And.Contain($"'{ElementName}'", "and on which control");
    }

    public static TheoryData<Exception> NotSupported => new()
    {
        new FlaUI.Core.Exceptions.PatternNotSupportedException(),
        new FlaUI.Core.Exceptions.PropertyNotSupportedException("RowCount", FlaUI.Core.Identifiers.PropertyId.NotSupportedByFramework),
        new FlaUI.Core.Exceptions.NotSupportedException("not supported"),
        new FlaUI.Core.Exceptions.MethodNotSupportedException(),
        new FlaUI.Core.Exceptions.NotSupportedByFrameworkException(),
        FlaUiShaped.From(FlaUiShaped.UIA_E_NOTSUPPORTED),
    };

    [Theory]
    [MemberData(nameof(NotSupported))]
    public void Translate_turns_pattern_or_property_not_supported_into_a_refusal_naming_the_control(Exception ex)
    {
        var translated = Translated(ex, action: "get_table");

        translated.Should().BeOfType<InvalidOperationException>();
        ToolErrors.IsCallerFacing(translated).Should().BeTrue();
        translated.Message.Should().ContainEquivalentOf("support");
        translated.Message.Should().Contain(ControlType, "the description promises the control type");
        translated.Message.Should().Contain($"'{ElementName}'", "and the name says which element it was");
    }

    public static TheoryData<Exception> Gone => new()
    {
        new FlaUI.Core.Exceptions.ElementNotAvailableException(),
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE),
    };

    [Theory]
    [MemberData(nameof(Gone))]
    public void Translate_turns_an_element_that_is_gone_into_a_refusal_naming_the_id_and_the_remedy(Exception ex)
    {
        var translated = Translated(ex);

        translated.Should().BeOfType<InvalidOperationException>();
        ToolErrors.IsCallerFacing(translated).Should().BeTrue();
        translated.Message.Should().Contain(Id, "the caller is told which of its ids died");
        translated.Message.Should().ContainEquivalentOf("no longer available");
        translated.Message.Should().Contain("snapshot", "and that the remedy is a new snapshot");
    }

    [Theory]
    [MemberData(nameof(NotEnabled))]
    [MemberData(nameof(NotSupported))]
    [MemberData(nameof(Gone))]
    public void Translate_keeps_the_original_as_the_inner_exception(Exception ex)
    {
        // The client reads the message; the server's log keeps the HRESULT and the FlaUI type.
        Translated(ex).InnerException.Should().BeSameAs(ex);
    }

    public static TheoryData<Exception> NotARefusal => new()
    {
        new NullReferenceException(),                                    // our bug: stays masked
        FlaUiShaped.From(FlaUiShaped.E_FAIL),                            // Win32Exception, HRESULT lost
        FlaUiShaped.From(FlaUiShaped.REGDB_E_CLASSNOTREG),
        // A target process that EXITED: FlaUI hands over the same HRESULT-less Win32Exception as
        // E_FAIL, so the exception alone cannot say "gone". The service's probe decides (R20).
        FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE),
        FlaUiShaped.From(FlaUiShaped.RPC_E_DISCONNECTED),
        new IndexOutOfRangeException(),
        new Exception("plain"),
    };

    [Theory]
    [MemberData(nameof(NotARefusal))]
    public void Translate_leaves_an_unexpected_fault_masked(Exception ex)
    {
        UiRefusals.Translate(ex, Id, "invoke", ControlType, ElementName).Should().BeNull(
            "translating a fault into a caller-facing type would leak internals and hide our own bug behind a 'refusal'");
    }

    public static TheoryData<Exception> AlreadyCallerFacing => new()
    {
        UiRefusals.PatternNotSupported("InvokePattern", ControlType, ElementName),
        new ArgumentException("'type' requires a value", "value"),
        new KeyNotFoundException("No item named 'x'"),
        new TimeoutException("slow"),
        FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT),   // FlaUI's own TimeoutException("UIA Timeout")
        new OperationCanceledException(),   // cancellation must propagate as itself, never as a refusal
    };

    [Theory]
    [MemberData(nameof(AlreadyCallerFacing))]
    public void Translate_passes_an_already_caller_facing_exception_through_untouched(Exception ex)
    {
        UiRefusals.Translate(ex, Id, "select", ControlType, ElementName).Should().BeNull(
            "null means 'rethrow as is': the service's own refusal already says the right thing");
    }

    // ---- D-10 R20: the liveness probe, fed what FlaUI actually throws --------------------------
    //
    // The "gone" decision used to rest on HRESULTs (RPC_E_DISCONNECTED, RPC_S_SERVER_UNAVAILABLE,
    // RPC_S_CALL_FAILED) that FlaUI never delivers: for a WPF / UWP / Chromium target whose PROCESS
    // EXITED, the body throws a Win32Exception with NativeErrorCode 0 and the probe's ProcessId read
    // throws the same — so the caller still saw the masked error. ProbeSaysGone is the probe's
    // decision without the element: a read that cannot complete means the provider is gone.

    [Theory]
    [InlineData(1234)]
    [InlineData(4)]          // System: any positive pid is a live provider
    [InlineData(int.MaxValue)]
    public void ProbeSaysGone_is_false_when_the_process_id_read_returns_a_positive_pid(int pid)
        => UiRefusals.ProbeSaysGone(() => pid).Should().BeFalse("a provider that answers with its pid is alive");

    [Theory]
    [InlineData(0)]          // the Win32 proxy on a destroyed HWND keeps answering, with ProcessId 0
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ProbeSaysGone_is_true_when_the_process_id_read_returns_zero_or_less(int pid)
        => UiRefusals.ProbeSaysGone(() => pid).Should().BeTrue();

    public static TheoryData<Exception> ProbeCannotComplete => new()
    {
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE),   // ElementNotAvailableException
        FlaUiShaped.From(FlaUiShaped.RPC_E_DISCONNECTED),          // the provider's process exited: Win32Exception, HRESULT lost
        FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE),
        FlaUiShaped.From(FlaUiShaped.RPC_S_CALL_FAILED),
        FlaUiShaped.From(FlaUiShaped.E_ACCESSDENIED),
        FlaUiShaped.From(FlaUiShaped.E_FAIL),
        new FlaUI.Core.Exceptions.ElementNotAvailableException(),
    };

    [Theory]
    [MemberData(nameof(ProbeCannotComplete))]
    public void ProbeSaysGone_is_true_when_the_process_id_read_cannot_complete(Exception thrown)
    {
        // Precondition, so the row cannot silently drift into testing something else: what FlaUI
        // hands over for an RPC failure really is an HRESULT-less Win32Exception.
        if (thrown is Win32Exception w32)
        {
            w32.NativeErrorCode.Should().Be(0, "precondition: FlaUI's Com.Call drops the HRESULT");
            w32.InnerException.Should().BeNull();
        }

        UiRefusals.ProbeSaysGone(() => throw thrown).Should().BeTrue(
            "a liveness read that cannot complete means the provider is gone; the alternative is the masked error D-10 removes");
    }

    [Fact]
    public void ProbeSaysGone_is_true_for_a_raw_COMException_as_the_contract_names()
    {
        // Defensive: no FlaUI UIA3 path delivers a raw COMException today (FlaUiShaped), but the
        // R20 contract lists it, and a FlaUI upgrade that stops converting must not re-mask "gone".
        UiRefusals.ProbeSaysGone(() => throw new COMException("rpc", unchecked((int)FlaUiShaped.RPC_S_SERVER_UNAVAILABLE)))
            .Should().BeTrue();
    }

    public static TheoryData<Exception> ProbeAnsweredSomethingElse => new()
    {
        FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT),             // a HUNG provider is not a dead one
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTENABLED),
        FlaUiShaped.From(FlaUiShaped.UIA_E_NOTSUPPORTED),
        new FlaUI.Core.Exceptions.PropertyNotSupportedException("ProcessId", FlaUI.Core.Identifiers.PropertyId.NotSupportedByFramework),
        new NullReferenceException(),
        new InvalidOperationException("x"),
    };

    [Theory]
    [MemberData(nameof(ProbeAnsweredSomethingElse))]
    public void ProbeSaysGone_is_false_for_any_other_exception(Exception thrown)
        => UiRefusals.ProbeSaysGone(() => throw thrown).Should().BeFalse(
            "only a read that could not complete or answered 'no process' means gone; anything else is not evidence of death");

    [Fact]
    public void ProbeSaysGone_lets_a_cancellation_propagate_as_the_same_instance()
    {
        var cancelled = new OperationCanceledException();

        var act = () => UiRefusals.ProbeSaysGone(() => throw cancelled);

        act.Should().Throw<OperationCanceledException>().Which.Should().BeSameAs(cancelled);
    }

    [Fact]
    public void ProbeSaysGone_reads_the_process_id_exactly_once()
    {
        // Every read is a cross-process call on the one shared STA thread.
        int reads = 0;

        UiRefusals.ProbeSaysGone(() => { reads++; return 42; });

        reads.Should().Be(1);
    }

    // ---- D-10 R22: a failing CHILD is not the container's death -------------------------------
    //
    // get_table on a grid whose cell or header dies mid-read, select with a value whose item dies
    // after Expand: the service has probed the TARGET and it is alive, so "Element 'el_5' is no
    // longer available" is a lie about el_5. Likewise a disabled item inside an enabled combo is not
    // "the control is disabled".

    public static TheoryData<Exception> GoneTypes => new()
    {
        new FlaUI.Core.Exceptions.ElementNotAvailableException(),
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE),
    };

    [Theory]
    [MemberData(nameof(GoneTypes))]
    public void Translate_with_a_live_target_blames_a_child_part_not_the_container(Exception ex)
    {
        var translated = UiRefusals.Translate(ex, Id, "get_table", ControlType, ElementName, targetIsAlive: true);

        translated.Should().BeOfType<InvalidOperationException>();
        ToolErrors.IsCallerFacing(translated!).Should().BeTrue();
        translated!.Message.Should().Contain(ControlType).And.Contain($"'{ElementName}'").And.Contain(Id,
            "the container is named by control type, name and id");
        translated.Message.Should().MatchRegex("(?i)\\b(item|part|child)\\b", "the thing that went away is a part of it");
        translated.Message.Should().ContainEquivalentOf("no longer available");
        translated.Message.Should().Contain("snapshot", "the remedy is still a new snapshot");
        translated.Message.Should().NotContain($"Element '{Id}' is no longer available",
            "el_N itself is alive: the service just probed it");
        translated.Message.Should().NotContainEquivalentOf("its window closed");
        translated.InnerException.Should().BeSameAs(ex);
    }

    [Theory]
    [MemberData(nameof(NotEnabled))]
    public void Translate_with_a_live_target_says_the_control_or_the_item_acted_on_is_disabled(Exception ex)
    {
        var translated = UiRefusals.Translate(ex, Id, "select", "ComboBox", "Fruit", targetIsAlive: true);

        translated.Should().BeOfType<InvalidOperationException>();
        translated!.Message.Should().ContainEquivalentOf("disabled");
        translated.Message.Should().ContainEquivalentOf("or the item being acted on",
            "a disabled ITEM inside an enabled combo is not 'the control is disabled'");
        translated.Message.Should().Contain("select").And.Contain("ComboBox").And.Contain("'Fruit'");
        translated.InnerException.Should().BeSameAs(ex);
    }

    [Theory]
    [MemberData(nameof(NotEnabled))]
    [MemberData(nameof(NotSupported))]
    [MemberData(nameof(Gone))]
    public void Translate_without_a_live_target_reads_exactly_as_before(Exception ex)
    {
        // targetIsAlive defaults to false and false is today's behaviour, word for word.
        var byDefault = UiRefusals.Translate(ex, Id, "invoke", ControlType, ElementName);
        var explicitFalse = UiRefusals.Translate(ex, Id, "invoke", ControlType, ElementName, targetIsAlive: false);

        explicitFalse!.Message.Should().Be(byDefault!.Message);
        explicitFalse.GetType().Should().Be(byDefault.GetType());
    }

    [Theory]
    [MemberData(nameof(Gone))]
    public void Translate_without_a_live_target_still_says_the_element_itself_is_gone(Exception ex)
    {
        UiRefusals.Translate(ex, Id, "invoke", ControlType, ElementName, targetIsAlive: false)!
            .Message.Should().Contain($"Element '{Id}' is no longer available");
    }

    [Theory]
    [MemberData(nameof(NotSupported))]
    public void Translate_with_a_live_target_leaves_a_not_supported_refusal_unchanged(Exception ex)
    {
        UiRefusals.Translate(ex, Id, "get_table", ControlType, ElementName, targetIsAlive: true)!.Message
            .Should().Be(UiRefusals.Translate(ex, Id, "get_table", ControlType, ElementName)!.Message);
    }

    [Theory]
    [MemberData(nameof(NotARefusal))]
    [MemberData(nameof(AlreadyCallerFacing))]
    public void Translate_with_a_live_target_still_leaves_faults_masked_and_refusals_untouched(Exception ex)
        => UiRefusals.Translate(ex, Id, "select", ControlType, ElementName, targetIsAlive: true).Should().BeNull();

    [Fact]
    public void ChildGone_names_the_container_and_says_an_item_of_it_went_away()
    {
        var inner = new FlaUI.Core.Exceptions.ElementNotAvailableException();

        var ex = UiRefusals.ChildGone("el_5", "select", "ComboBox", "Fruit", inner);

        ex.Should().BeOfType<InvalidOperationException>();
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("el_5").And.Contain("ComboBox").And.Contain("'Fruit'");
        ex.Message.Should().Contain("select", "the refusal names the action");
        ex.Message.Should().MatchRegex("(?i)\\b(item|part|child)\\b");
        ex.Message.Should().ContainEquivalentOf("no longer available");
        ex.Message.Should().Contain("snapshot");
        ex.Message.Should().NotContain("Element 'el_5' is no longer available");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void ChildGone_without_an_inner_exception_has_none()
        => UiRefusals.ChildGone("el_5", "select", "ComboBox", "Fruit").InnerException.Should().BeNull();

    // ---- D-10 R23: a hung target is not probed -------------------------------------------------
    //
    // On a TimeoutException from the body, Refusing's catch filter still ran the liveness probe and
    // two property reads — three more cross-process calls, each up to the UIA timeout, on the one
    // shared STA thread — then rethrew the same exception. NeedsProbe is the filter's first question.

    public static TheoryData<Exception> NeverProbed => new()
    {
        new TimeoutException("slow"),
        FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT),
        new OperationCanceledException(),
        new TaskCanceledException(),
        new ArgumentException("'type' requires a value", "value"),
        new ArgumentNullException("value"),
        new ArgumentOutOfRangeException("value"),
    };

    [Theory]
    [MemberData(nameof(NeverProbed))]
    public void NeedsProbe_is_false_for_a_timeout_a_cancellation_and_an_argument_error(Exception ex)
        => UiRefusals.NeedsProbe(ex).Should().BeFalse(
            "these are rethrown as they are; reading the element again only stalls the STA thread on a hung target");

    public static TheoryData<Exception> Probed => new()
    {
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE),
        FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTENABLED),
        FlaUiShaped.From(FlaUiShaped.UIA_E_NOTSUPPORTED),
        FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE),   // the only way to learn the target died is to probe
        new FlaUI.Core.Exceptions.PatternNotSupportedException(),
        new KeyNotFoundException("No item named 'x'"),              // a dead combo has no children: liveness first
        UiRefusals.PatternNotSupported("InvokePattern", ControlType, ElementName),
    };

    [Theory]
    [MemberData(nameof(Probed))]
    public void NeedsProbe_is_true_for_what_the_probe_can_reinterpret(Exception ex)
        => UiRefusals.NeedsProbe(ex).Should().BeTrue();

    // ---- E-2 round 2 (C): Clip --------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(79)]
    [InlineData(80)]
    public void Clip_leaves_up_to_80_characters_untouched(int length)
    {
        var text = new string('n', length);

        UiRefusals.Clip(text).Should().BeSameAs(text);
    }

    [Theory]
    [InlineData(81)]
    [InlineData(3000)]
    public void Clip_cuts_at_80_and_appends_three_ascii_dots(int length)
        => UiRefusals.Clip(new string('n', length)).Should().Be(new string('n', 80) + "...");

    [Fact]
    public void Clip_never_splits_a_surrogate_pair_at_the_cut()
    {
        // 79 chars, then a pair straddling the 80th/81st position: the cut moves back one so the
        // quoted name never ends in a lone high surrogate (which JSON would turn into U+FFFD).
        var text = new string('n', 79) + "\U0001F600" + "tail";

        var clipped = UiRefusals.Clip(text);

        clipped.Should().Be(new string('n', 79) + "...");
        char.IsHighSurrogate(clipped[78]).Should().BeFalse();
    }

    [Fact]
    public void Clip_keeps_a_surrogate_pair_that_ends_exactly_at_80()
        => UiRefusals.Clip(new string('n', 78) + "\U0001F600" + "tail").Should().Be(new string('n', 78) + "\U0001F600" + "...");

    [Fact]
    public void MaxQuoted_is_80()
        => UiRefusals.MaxQuoted.Should().Be(80);
}
