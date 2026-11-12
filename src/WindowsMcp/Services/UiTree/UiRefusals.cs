using System.Runtime.InteropServices;
using FlaUI.Core.Exceptions;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// D-10: the refusals the element tools raise, built without a live <c>AutomationElement</c> so
/// their type and wording are testable headless. Every one is a type
/// <c>ToolErrors.IsCallerFacing</c> accepts: a refusal the caller cannot read is indistinguishable
/// from a crash.
/// </summary>
internal static class UiRefusals
{
    private const int UIA_E_ELEMENTNOTENABLED = unchecked((int)0x80040200);
    private const int UIA_E_NOTSUPPORTED = unchecked((int)0x80040204);

    /// <summary>How much of a control's name, or of a value the caller sent, a refusal quotes.</summary>
    internal const int MaxQuoted = 80;

    /// <summary>
    /// A UI-supplied or caller-supplied string as a refusal quotes it: at most
    /// <see cref="MaxQuoted"/> characters, then "...". Error messages are capped at 2 000
    /// characters, and what to do next comes after the name.
    /// </summary>
    internal static string Clip(string text)
    {
        if (text.Length <= MaxQuoted) return text;
        int cut = char.IsHighSurrogate(text[MaxQuoted - 1]) ? MaxQuoted - 1 : MaxQuoted;
        return text[..cut] + "...";
    }

    /// <summary>
    /// The element does not support <paramref name="pattern"/>. An
    /// <see cref="InvalidOperationException"/> because it is a deliberate refusal the caller must
    /// read: <c>ToolErrors.IsCallerFacing</c> does not list <see cref="System.NotSupportedException"/>, so
    /// that type reached the client as "An error occurred invoking 'interact_element'.".
    /// </summary>
    public static InvalidOperationException PatternNotSupported(string pattern, string controlType, string name)
        => new($"{pattern} not supported on {controlType} '{name}'.");

    /// <summary>The element behind <paramref name="elementId"/> was destroyed after the id was issued.</summary>
    public static InvalidOperationException Gone(string elementId, Exception? inner = null)
        => new($"Element '{elementId}' is no longer available: its window closed or the control was destroyed. " +
               "Take a new snapshot and use the id it returns.", inner);

    /// <summary>
    /// D-10 R11: what the TARGET threw, as something the caller can read — or null to rethrow
    /// <paramref name="ex"/> as it is. UI Automation reports a disabled control, a missing pattern
    /// or property and a destroyed element through FlaUI exception types (all
    /// <c>FlaUIException : Exception</c>) or raw COM HRESULTs, none of them caller-facing. Null for
    /// an exception that already is caller-facing, for a cancellation, and for anything
    /// unrecognised: an unexpected fault must stay masked, not be dressed up as a refusal.
    /// </summary>
    /// <param name="targetIsAlive">
    /// R22: the caller has probed the target element and it is alive, so whatever failed is a
    /// PART of it (a grid cell, a header, a list item). The refusal then blames that part: telling
    /// the caller a live <c>el_N</c> "is no longer available" sends it into the same failure again.
    /// </param>
    public static Exception? Translate(Exception ex, string elementId, string action, string controlType, string name,
        bool targetIsAlive = false)
    {
        if (ex is OperationCanceledException || ToolErrors.IsCallerFacing(ex)) return null;

        int? hresult = ex is COMException com ? com.HResult : null;

        if (ex is ElementNotEnabledException || hresult == UIA_E_ELEMENTNOTENABLED)
            return new InvalidOperationException(targetIsAlive
                ? $"Cannot {action} {controlType} '{name}': the control, or the item being acted on, is disabled."
                : $"Cannot {action} {controlType} '{name}': the control is disabled.", ex);

        if (UIAutomationService.IsElementGone(ex))
            return targetIsAlive ? ChildGone(elementId, action, controlType, name, ex) : Gone(elementId, ex);

        if (ex is PatternNotSupportedException or PropertyNotSupportedException or MethodNotSupportedException
                or NotSupportedByFrameworkException or FlaUI.Core.Exceptions.NotSupportedException
            || hresult == UIA_E_NOTSUPPORTED)
            return new InvalidOperationException(
                $"{controlType} '{name}' does not support what {action} needs: {ex.Message}", ex);

        return null;
    }

    /// <summary>
    /// R22: the target is alive but something inside it went away while it was being worked on —
    /// a virtualised grid cell, a header, a list item whose popup closed.
    /// </summary>
    public static InvalidOperationException ChildGone(string elementId, string action, string controlType, string name, Exception? inner = null)
        => new($"Cannot {action} {controlType} '{name}' ({elementId}): an item or part of it is no longer available; " +
               "it changed while being read. Take a new snapshot and retry.", inner);

    /// <summary>
    /// R20: the liveness verdict from one read of the element's process id. A destroyed Win32
    /// control keeps answering with defaults and reports pid 0. A provider whose PROCESS exited
    /// cannot answer at all, and FlaUI delivers that as <see cref="ElementNotAvailableException"/>
    /// or — for the RPC failures, whose HRESULT it discards — a bare
    /// <see cref="System.ComponentModel.Win32Exception"/>; a liveness read that cannot complete
    /// means there is nothing left to read. Any other failure is not evidence of death.
    /// </summary>
    internal static bool ProbeSaysGone(Func<int> readProcessId)
    {
        try
        {
            return readProcessId() <= 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ex is ElementNotAvailableException or System.ComponentModel.Win32Exception or COMException;
        }
    }

    /// <summary>
    /// R23: whether a failure is worth re-reading the element for. A timeout means the target is
    /// hung — each further read costs the UIA timeout again on the one STA thread every element
    /// tool shares — and a cancellation or an argument error is about the call, not the element.
    /// Those are rethrown as they are.
    /// </summary>
    internal static bool NeedsProbe(Exception ex)
        => ex is not (OperationCanceledException or TimeoutException or ArgumentException);
}
