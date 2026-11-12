using System.Runtime.InteropServices;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class PowerService : IPowerService
{
    private readonly IPowerNative _native;

    public PowerService() : this(new Win32PowerNative()) { }

    /// <summary>D-10 R15: the native calls are behind a seam so the refusal path is testable without a power action.</summary>
    internal PowerService(IPowerNative native) => _native = native;

    public Task ExecuteAsync(string action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var name = action.ToLowerInvariant();
        bool ok;
        int error;
        switch (name)
        {
            case "shutdown":
                _native.EnableShutdownPrivilege();
                ok = _native.ExitWindows(PowerExitKind.Shutdown, out error);
                break;
            case "reboot":
                _native.EnableShutdownPrivilege();
                ok = _native.ExitWindows(PowerExitKind.Reboot, out error);
                break;
            case "logoff":
                // Logoff needs no special privilege, but still verify the call succeeded.
                ok = _native.ExitWindows(PowerExitKind.Logoff, out error);
                break;
            case "lock":
                ok = _native.LockWorkStation(out error);
                break;
            case "sleep":
                ok = _native.SetSuspendState(hibernate: false, out error);
                break;
            case "hibernate":
                ok = _native.SetSuspendState(hibernate: true, out error);
                break;
            default:
                throw new ArgumentException($"Unknown power action '{action}'; expected shutdown|reboot|logoff|lock|sleep|hibernate");
        }

        if (!ok) throw Refused(name, error);
        return Task.CompletedTask;
    }

    /// <summary>
    /// D-10 R15: the OS said no. That is an answer the caller has to read (hibernation switched
    /// off, no interactive desktop to lock, a shutdown already under way), so it is a caller-facing
    /// type carrying the action and Windows' own reason — as a Win32Exception it was masked.
    /// </summary>
    private static InvalidOperationException Refused(string action, int error)
    {
        // Marshal.GetPInvokeErrorMessage is the text Win32Exception(error).Message carries.
        var reason = error != 0
            ? $"{Marshal.GetPInvokeErrorMessage(error)} (Win32 error {error})"
            : "the call failed and Windows reported no error code";
        var hint = action switch
        {
            "sleep" => " Suspend may be disabled on this machine.",
            "hibernate" => " Hibernation may be disabled on this machine (powercfg /h).",
            "lock" => " Locking needs an interactive desktop session.",
            _ => "",
        };
        return new InvalidOperationException($"Windows refused the power action '{action}': {reason}.{hint}");
    }
}
