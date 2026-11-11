using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Shutdown;

namespace WindowsMcp.Services;

/// <summary>
/// D-10 R15: the real power calls behind <see cref="IPowerNative"/>. Each action reports failure
/// as <c>false</c> plus the Win32 error, so <see cref="PowerService"/> decides what the caller is
/// told; only the token plumbing throws from here.
/// </summary>
internal sealed class Win32PowerNative : IPowerNative
{
    public bool ExitWindows(PowerExitKind kind, out int error)
    {
        var flags = kind switch
        {
            PowerExitKind.Shutdown => EXIT_WINDOWS_FLAGS.EWX_SHUTDOWN,
            PowerExitKind.Reboot => EXIT_WINDOWS_FLAGS.EWX_REBOOT,
            _ => EXIT_WINDOWS_FLAGS.EWX_LOGOFF,
        };
        return Report(PInvoke.ExitWindowsEx(flags, 0), out error);
    }

    public bool LockWorkStation(out int error) => Report(PInvoke.LockWorkStation(), out error);

    public bool SetSuspendState(bool hibernate, out int error)
        => Report(PInvoke.SetSuspendState(hibernate, false, false), out error);

    private static bool Report(bool ok, out int error)
    {
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    // ExitWindowsEx(SHUTDOWN|REBOOT) silently no-ops unless SeShutdownPrivilege is ENABLED in the
    // process token. The interactive user holds the privilege; it just needs enabling each run.
    // The Win32Exceptions here are faults in our own token plumbing, masked on purpose
    // (ToolErrorsTests.MaskedOnPurpose names this method); the privilege not being held is a
    // refusal and says so.
    public unsafe void EnableShutdownPrivilege()
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(
                PInvoke.GetCurrentProcess(),
                TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY,
                &token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");

        try
        {
            if (!PInvoke.LookupPrivilegeValue(null, "SeShutdownPrivilege", out LUID luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed");

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            tp.Privileges[0] = new LUID_AND_ATTRIBUTES
            {
                Luid = luid,
                Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED
            };

            if (!PInvoke.AdjustTokenPrivileges(token, false, &tp, 0, null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed");

            // AdjustTokenPrivileges returns true even when the privilege was not assigned;
            // ERROR_NOT_ALL_ASSIGNED (1300) means the token does not hold SeShutdownPrivilege.
            const int ERROR_NOT_ALL_ASSIGNED = 1300;
            if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                throw new InvalidOperationException(
                    "SeShutdownPrivilege is not held by this process token; cannot shut down or reboot.");
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }
}
