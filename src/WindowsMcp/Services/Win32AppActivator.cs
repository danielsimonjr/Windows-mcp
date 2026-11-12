using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsMcp.Services;

/// <summary>
/// B-8: the two ways an app is started. A packaged (Store/MSIX) app has no executable to run —
/// it is activated by its AppUserModelId through the shell's activation manager, which is the
/// one API that hands back the process id the window wait needs. A shortcut or a path goes
/// through ShellExecute, which resolves <c>.lnk</c> files and PATH.
/// </summary>
internal sealed class Win32AppActivator : IAppActivator
{
    internal static Win32AppActivator Instance { get; } = new();

    public int ActivatePackaged(string aumid)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            int hr = manager.ActivateApplication(aumid, null, ACTIVATEOPTIONS.None, out uint pid);
            // D-10 R12: built from the HRESULT itself. Marshal.ThrowExceptionForHR maps it to an
            // arbitrary type — a masked COMException, or an ArgumentException naming nothing.
            if (hr < 0) throw ProcessStartErrors.ActivationFailed(aumid, hr);
            return (int)pid;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    public int StartShortcutOrPath(string target)
    {
        using var process = ProcessStartErrors.Start(new ProcessStartInfo(target) { UseShellExecute = true }, target);
        return process.Id;
    }

    private enum ACTIVATEOPTIONS { None = 0 }

    // Declared leading method only, in vtable order (CLAUDE.md's COM rule): ActivateForFile and
    // ActivateForProtocol follow it and are never called, so they are not declared.
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ACTIVATEOPTIONS options,
            out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
}
