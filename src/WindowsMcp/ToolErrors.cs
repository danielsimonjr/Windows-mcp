namespace WindowsMcp;

/// <summary>
/// Classifies which exceptions are <b>intentional, caller-facing failures</b> whose messages must
/// reach the caller.
/// </summary>
/// <remarks>
/// The MCP SDK masks every exception that isn't an <c>McpException</c>, returning a bare
/// <c>"An error occurred invoking '&lt;tool&gt;'."</c>. That is a sane default for unexpected faults
/// — it avoids leaking internals — but it is actively harmful for our *deliberate* refusals, whose
/// messages are the entire point:
/// <list type="bullet">
///   <item>the PID-reuse start-time guard aborting a kill ("start time … != expected …; aborting"),</item>
///   <item>a destructive action refused for want of <c>confirm: true</c>,</item>
///   <item>a parameter combination we reject rather than silently ignore.</item>
/// </list>
/// Masked, a guard abort is indistinguishable from a crash — so a caller may well "retry" the kill
/// without the guard, which is precisely the outcome the guard exists to prevent. These we surface;
/// anything else keeps the SDK's masking.
/// </remarks>
internal static class ToolErrors
{
    /// <summary>
    /// True when the exception is a deliberate refusal or a bad-input rejection raised by our own
    /// tools/services (including <c>Process.GetProcessById</c>'s "process is not running"), rather
    /// than an unexpected fault.
    /// </summary>
    public static bool IsCallerFacing(Exception ex) =>
        ex is ArgumentException or InvalidOperationException;
}
