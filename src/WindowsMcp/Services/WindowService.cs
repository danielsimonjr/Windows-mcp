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

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated STA thread and returns its result.
    ///
    /// UIAutomation is COM and requires an STA apartment. MCP tool calls arrive on MTA thread-pool
    /// threads, where FlaUI's desktop enumeration quietly yields nothing - which is why an earlier
    /// UIA fallback here "found" no windows while <see cref="UIAutomationService"/>, which owns a
    /// long-lived STA worker, listed them fine in the same process. COM objects are apartment-bound,
    /// so the whole find-and-act sequence must happen inside ONE call, not just the lookup.
    /// </summary>
    private static T RunOnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "WindowsMcp-WindowLookup-STA",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return failure is not null ? throw failure : result;
    }

    /// <summary>
    /// Finds a top-level window through UIAutomation and focuses it, entirely on one STA thread.
    ///
    /// Used when USER32 enumeration comes up empty, which happens whenever this server is started
    /// from a service-hosted parent: EnumWindows is scoped to the caller's window station and
    /// desktop and returns NONE of the interactive desktop's windows there, while UIAutomation
    /// reaches them through its own COM service. Measured 2026-09-17 inside one server process,
    /// seconds apart: `focus` missed a window by its byte-exact caption while `get_state` returned
    /// that same caption.
    ///
    /// Deliberately does not require NativeWindowHandle: UIA commonly reports it as 0, and an
    /// earlier version that insisted on a handle discarded every candidate and still said
    /// "not found".
    /// </summary>
    private static bool TryFocusViaUIAutomation(string title)
    {
        return RunOnSta(() =>
        {
            using var automation = new FlaUI.UIA3.UIA3Automation();
            var elements = new List<FlaUI.Core.AutomationElements.AutomationElement>();
            var candidates = new List<WindowCandidate>();

            foreach (var child in automation.GetDesktop().FindAllChildren())
            {
                string name;
                try { name = child.Properties.Name.ValueOrDefault ?? string.Empty; }
                catch { continue; }   // a window can vanish mid-enumeration; skip only that one

                if (string.IsNullOrEmpty(name))
                    continue;

                long area = 0;
                bool visible = true;
                try
                {
                    var r = child.Properties.BoundingRectangle.ValueOrDefault;
                    area = (long)Math.Max(0, r.Width) * (long)Math.Max(0, r.Height);
                    visible = !child.Properties.IsOffscreen.ValueOrDefault;
                }
                catch { /* geometry only affects ranking; its absence must not drop the candidate */ }

                // The index rides in the handle field so the shared matcher can rank these exactly
                // as it ranks USER32 results.
                candidates.Add(new WindowCandidate(elements.Count, name, visible, area));
                elements.Add(child);
            }

            WindowCandidate? match = WindowMatcher.Select(candidates, title);
            if (match is null)
                return false;

            var element = elements[(int)match.Value.Handle];

            if (element.Patterns.Window.TryGetPattern(out var windowPattern)
                && windowPattern.WindowVisualState.ValueOrDefault == FlaUI.Core.Definitions.WindowVisualState.Minimized)
            {
                // Focusing a minimized window leaves it minimized, so the call would report success
                // while nothing visibly changed.
                windowPattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
            }

            element.Focus();
            return true;
        });
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

    public Task<WindowFocusResult> SwitchToAsync(string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        HWND hwnd = ResolveWindow(title);
        if (hwnd != HWND.Null)
        {
            // A minimized window stays minimized when it is merely foregrounded, so the call would
            // report success while the caller sees nothing change. Restore first.
            if (PInvoke.IsIconic(hwnd))
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

            bool activated = PInvoke.SetForegroundWindow(hwnd);

            // Windows only lets the CURRENT foreground process hand focus away, so a background
            // server is refused here as a matter of policy, not error. Raising the window is the
            // honest best effort, and UIA can often complete the activation where USER32 cannot.
            if (!activated)
            {
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOW);
                activated = TryFocusViaUIAutomation(title);
            }

            return Task.FromResult(new WindowFocusResult(true, activated));
        }

        // USER32 found nothing. Try UIA once; if it focuses the window, it also found it.
        bool viaUia = TryFocusViaUIAutomation(title);
        return Task.FromResult(new WindowFocusResult(viaUia, viaUia));
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
