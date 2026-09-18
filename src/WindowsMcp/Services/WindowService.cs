using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WindowService : IWindowService
{
    private const uint WM_CLOSE = 0x0010;
    private const uint MONITORINFOF_PRIMARY = 1u;

    /// <summary>
    /// Enumerates top-level windows and picks the one the caller meant.
    ///
    /// Replaces <c>FindWindow(null, title)</c>, which demands an EXACT full-title match. That made
    /// the tool unable to focus windows it could plainly see: measured 2026-09-17, `focus` reported
    /// "not found" for a caption that EnumWindows confirmed was visible with that byte-exact
    /// string, while `get_state` listed the same window through UIAutomation. Titles also carry
    /// live state - Obsidian's starts "New tab - " and changes as tabs change - so an exact match
    /// is a race even when the caller copies the caption verbatim.
    ///
    /// Ranking lives in <see cref="WindowMatcher"/> so it is testable without a desktop.
    /// </summary>
    private static HWND ResolveWindow(string title)
    {
        var candidates = new List<WindowCandidate>();

        PInvoke.EnumWindows((hwnd, _) =>
        {
            int length = PInvoke.GetWindowTextLength(hwnd);
            if (length > 0)
            {
                Span<char> buffer = stackalloc char[length + 1];
                unsafe
                {
                    fixed (char* p = buffer)
                    {
                        int copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
                        if (copied > 0)
                        {
                            bool visible = PInvoke.IsWindowVisible(hwnd);
                            long area = 0;
                            if (PInvoke.GetWindowRect(hwnd, out RECT rect))
                            {
                                long w = Math.Max(0, rect.right - rect.left);
                                long h = Math.Max(0, rect.bottom - rect.top);
                                area = w * h;
                            }

                            candidates.Add(new WindowCandidate(
                                (nint)hwnd.Value, new string(p, 0, copied), visible, area));
                        }
                    }
                }
            }

            return true;
        }, default);

        WindowCandidate? match = WindowMatcher.Select(candidates, title);
        return match is null ? HWND.Null : (HWND)match.Value.Handle;
    }

    public Task<WindowAction> ExecuteAsync(string action, string? title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required for window action", nameof(title));

        HWND hwnd = ResolveWindow(title);
        bool found = hwnd != HWND.Null;

        if (found)
        {
            switch (action.ToLowerInvariant())
            {
                case "minimize":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
                    break;
                case "maximize":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MAXIMIZE);
                    break;
                case "restore":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
                    break;
                case "close":
                    // CloseWindow() actually minimizes; PostMessage WM_CLOSE performs a real close.
                    PInvoke.PostMessage(hwnd, WM_CLOSE, default, default);
                    break;
                default:
                    throw new ArgumentException($"Unknown action '{action}'; expected minimize|maximize|restore|close");
            }
        }

        return Task.FromResult(new WindowAction(action, title, found));
    }

    public Task<bool> SwitchToAsync(string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        HWND hwnd = ResolveWindow(title);
        if (hwnd == HWND.Null)
            return Task.FromResult(false);

        // A minimized window stays minimized when it is merely foregrounded, so the call would
        // report success while the caller sees nothing change. Restore first.
        if (PInvoke.IsIconic(hwnd))
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

        bool ok = PInvoke.SetForegroundWindow(hwnd);
        return Task.FromResult(ok);
    }

    public Task<int> LaunchAsync(string appName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = appName,
            UseShellExecute = true
        };

        // Dispose our wrapper handle; the launched app keeps running independently.
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch '{appName}'");

        return Task.FromResult(process.Id);
    }

    public unsafe Task<MonitorInfo[]> EnumerateMonitorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var monitors = new List<(HMONITOR Handle, int Order)>();
        int index = 0;

        PInvoke.EnumDisplayMonitors(default, null,
            (hMonitor, hdcMonitor, lprcMonitor, lParam) =>
            {
                monitors.Add((hMonitor, index++));
                return true;
            },
            default);

        var results = new List<MonitorInfo>();
        foreach (var (handle, order) in monitors)
        {
            var info = new MONITORINFO
            {
                cbSize = (uint)sizeof(MONITORINFO)
            };

            if (PInvoke.GetMonitorInfo(handle, ref info))
            {
                var rc = info.rcMonitor;
                bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

                results.Add(new MonitorInfo(
                    order,
                    $"Monitor{order}",
                    rc.left,
                    rc.top,
                    rc.right - rc.left,
                    rc.bottom - rc.top,
                    isPrimary));
            }
        }

        return Task.FromResult(results.ToArray());
    }
}
