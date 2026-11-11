using System.Runtime.InteropServices;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// D-10 R20: what a UIA failure looks like by the time it reaches our code. Every FlaUI 5.0.0
/// UIA3 element, property and pattern call runs through <c>FlaUI.Core.Tools.Com.Call</c>, which
/// converts the HRESULT: UIA_E_ELEMENTNOTAVAILABLE → <c>ElementNotAvailableException</c>,
/// UIA_E_ELEMENTNOTENABLED → <c>ElementNotEnabledException</c>, UIA_E_NOTSUPPORTED → FlaUI's
/// <c>NotSupportedException</c>, UIA_E_TIMEOUT → <see cref="TimeoutException"/> — and EVERYTHING
/// ELSE (RPC_E_DISCONNECTED, RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED, E_ACCESSDENIED, E_FAIL, …)
/// → <see cref="System.ComponentModel.Win32Exception"/> with NativeErrorCode 0, HResult E_FAIL and
/// no inner exception: the HRESULT is gone. A test that feeds production a raw
/// <see cref="COMException"/> tests an input production never receives. This runs the real
/// conversion, so a FlaUI upgrade that changes it changes the inputs too.
/// </summary>
internal static class FlaUiShaped
{
    public const uint UIA_E_ELEMENTNOTENABLED = 0x80040200;
    public const uint UIA_E_ELEMENTNOTAVAILABLE = 0x80040201;
    public const uint UIA_E_NOTSUPPORTED = 0x80040204;
    public const uint UIA_E_TIMEOUT = 0x80131505;
    public const uint RPC_E_DISCONNECTED = 0x80010108;
    public const uint RPC_S_SERVER_UNAVAILABLE = 0x800706BA;
    public const uint RPC_S_CALL_FAILED = 0x800706BE;
    public const uint E_ACCESSDENIED = 0x80070005;
    public const uint E_FAIL = 0x80004005;
    public const uint REGDB_E_CLASSNOTREG = 0x80040154;

    /// <summary>The exception FlaUI's <c>Com.Call</c> throws when the UIA call fails with <paramref name="hresult"/>.</summary>
    public static Exception From(uint hresult)
    {
        try
        {
            // The HRESULT goes in the message only so a theory row's display name says which code it
            // was: the Win32Exception FlaUI builds keeps the message and drops the code.
            FlaUI.Core.Tools.Com.Call(() => throw new COMException($"uia call failed 0x{hresult:X8}", unchecked((int)hresult)));
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"Com.Call did not throw for 0x{hresult:X8}");
    }
}
