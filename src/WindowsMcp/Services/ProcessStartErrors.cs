using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsMcp.Services;

/// <summary>
/// D-10 R12: what a failed start tells the caller. <c>Process.Start</c> reports "no such file",
/// "access denied" and every other OS refusal as a <see cref="Win32Exception"/>, and packaged
/// activation reports an HRESULT; neither is a type <c>ToolErrors.IsCallerFacing</c> accepts, so a
/// mistyped command read as "An error occurred invoking 'start_process'.".
/// </summary>
internal static class ProcessStartErrors
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>
    /// The caller-facing form of a start failure, or null when <paramref name="ex"/> is not one
    /// (rethrow it as it is: already caller-facing, a cancellation, or a fault that stays masked).
    /// </summary>
    /// <param name="notFoundHint">Appended to a not-found refusal: what the caller can change.</param>
    public static Exception? Translate(Exception ex, string what, string? notFoundHint = null) => ex switch
    {
        Win32Exception { NativeErrorCode: ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND } =>
            new FileNotFoundException(
                $"Could not start '{what}': the file or command was not found. " +
                (notFoundHint ?? "Check the name, or give a full path."), ex),
        Win32Exception { NativeErrorCode: ERROR_ACCESS_DENIED } =>
            new UnauthorizedAccessException($"Could not start '{what}': access is denied.", ex),
        Win32Exception os =>
            new InvalidOperationException($"Could not start '{what}': {os.Message}", ex),
        COMException com =>
            new InvalidOperationException(ActivationMessage(what, com.HResult), ex),
        _ => null,
    };

    /// <summary>A failed packaged-app activation, built from the HRESULT itself.</summary>
    public static InvalidOperationException ActivationFailed(string aumid, int hresult)
        => new(ActivationMessage(aumid, hresult));

    private static string ActivationMessage(string what, int hresult)
        => $"Could not start '{what}': activation failed with HRESULT 0x{hresult:X8} ({Marshal.GetPInvokeErrorMessage(hresult)}).";

    /// <summary>
    /// <c>Process.Start</c> with its failures translated. A null process (ShellExecute handed the
    /// target to something already running) is reported as it always was.
    /// </summary>
    public static Process Start(ProcessStartInfo startInfo, string what, string? notFoundHint = null)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start '{what}': no new process was created.");
        }
        catch (Exception ex) when (Translate(ex, what, notFoundHint) is { } refusal)
        {
            throw refusal;
        }
    }
}
