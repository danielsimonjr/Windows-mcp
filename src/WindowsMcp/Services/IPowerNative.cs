namespace WindowsMcp.Services;

/// <summary>
/// D-10 R15: the user32/powrprof/advapi32 calls behind <c>power</c>, behind a seam so the branch
/// where the OS REFUSES (sleep with suspend disabled, hibernate with <c>powercfg /h off</c>, lock
/// with no interactive desktop) is testable without ever invoking a power action.
/// </summary>
// The production class is Win32PowerNative, whose EnableShutdownPrivilege is the one agreed
// Win32Exception site — ToolErrorsTests.MaskedOnPurpose names it.
internal interface IPowerNative
{
    /// <summary>
    /// Enable SeShutdownPrivilege in this process's token. Token plumbing failing is a genuine
    /// fault: it throws <c>Win32Exception</c>, which stays masked.
    /// </summary>
    void EnableShutdownPrivilege();

    /// <summary><c>ExitWindowsEx</c>; false with the Win32 error when the OS refused.</summary>
    bool ExitWindows(PowerExitKind kind, out int error);

    /// <summary><c>LockWorkStation</c>; false with the Win32 error when the OS refused.</summary>
    bool LockWorkStation(out int error);

    /// <summary><c>SetSuspendState(hibernate, false, false)</c>; false with the Win32 error when the OS refused.</summary>
    bool SetSuspendState(bool hibernate, out int error);
}

/// <summary>D-10 R15: which <c>ExitWindowsEx</c> request.</summary>
internal enum PowerExitKind { Shutdown, Reboot, Logoff }
