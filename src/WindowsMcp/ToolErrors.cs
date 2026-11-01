namespace WindowsMcp;

/// <summary>
/// Classifies which exceptions are <b>intentional, caller-facing failures</b> whose messages must
/// reach the caller, and bounds what reaches them.
/// </summary>
/// <remarks>
/// The MCP SDK masks every exception that isn't an <c>McpException</c>, returning a bare
/// <c>"An error occurred invoking '&lt;tool&gt;'."</c>. That is a sane default for unexpected faults
/// — it avoids leaking internals — but it is actively harmful for our *deliberate* answers, whose
/// messages are the entire point:
/// <list type="bullet">
///   <item>the PID-reuse start-time guard aborting a kill ("start time … != expected …; aborting"),</item>
///   <item>a destructive action refused for want of <c>confirm: true</c>,</item>
///   <item>a parameter combination we reject rather than silently ignore,</item>
///   <item>a lookup that found nothing and says what exists instead — the window matcher's
///   "Open windows: …", an element id no longer in the cache, a registry key that is not there,
///   an app the catalog does not know (C-1 round 4: these were all masked until then),</item>
///   <item>the file system's own answer — not found, in use, access denied, path too long — which
///   is what a file tool exists to report, with the path in it,</item>
///   <item>a wait that timed out.</item>
/// </list>
/// Masked, a guard abort is indistinguishable from a crash — so a caller may well "retry" the kill
/// without the guard, which is precisely the outcome the guard exists to prevent. These we surface;
/// anything else (a null reference, an index out of range, a COM or Win32 failure) keeps the
/// SDK's masking. A surfaced message is capped (<see cref="MessageFor"/>): a path-too-long error
/// quotes the whole path, which is exactly the thing that was too long.
/// </remarks>
internal static class ToolErrors
{
    /// <summary>The longest message a client is handed; a longer one is cut and says so.</summary>
    internal const int MaxMessageLength = 2000;

    /// <summary>
    /// True when the exception is a deliberate refusal, a bad-input rejection, a lookup miss or a
    /// file-system answer raised by our own tools/services (including
    /// <c>Process.GetProcessById</c>'s "process is not running"), rather than an unexpected fault.
    /// </summary>
    public static bool IsCallerFacing(Exception ex) =>
        ex is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException
            or IOException            // FileNotFound, DirectoryNotFound, PathTooLong, "in use"
            or UnauthorizedAccessException
            or TimeoutException
            or NotSupportedException;

    /// <summary>The message the client sees: word for word up to <see cref="MaxMessageLength"/>, then cut with a marker.</summary>
    public static string MessageFor(Exception ex)
    {
        var message = ex.Message;
        if (message.Length <= MaxMessageLength) return message;
        const string marker = " [cut: 2000-character limit] …";
        int cut = MaxMessageLength - marker.Length;
        if (char.IsHighSurrogate(message[cut - 1])) cut--;   // never split a surrogate pair
        return message[..cut] + marker;
    }
}
