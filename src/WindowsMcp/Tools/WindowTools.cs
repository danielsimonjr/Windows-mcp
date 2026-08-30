using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    private readonly IWindowService _window;

    public WindowTools(IWindowService window)
    {
        _window = window;
    }

    [McpServerTool, Description("Execute a window action (minimize, maximize, restore, close) on a window identified by title.")]
    public async Task<string> Window(
        [Description("Action: minimize, maximize, restore, or close")] string action,
        [Description("Window title text to find")] string? title = null)
    {
        var result = await _window.ExecuteAsync(action, title);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Switch focus to a window identified by its title using SetForegroundWindow.")]
    public async Task<string> SwitchToWindow(
        [Description("Window title to bring to foreground")] string title)
    {
        bool ok = await _window.SwitchToAsync(title);
        return ok ? $"switched to '{title}'" : $"window '{title}' not found";
    }

    [McpServerTool, Description("Launch an application by name or path. Uses ShellExecute so Start Menu shortcuts and PATH are resolved. Requires confirm:true.")]
    public async Task<string> Launch(
        [Description("Application name or executable path to launch")] string app_name,
        [Description("Must be true to confirm launching a process")] bool confirm = false)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for launch");
        int pid = await _window.LaunchAsync(app_name);
        return $"launched (pid={pid})";
    }

    [McpServerTool, Description("Set keyboard focus to a window identified by title (alias for switch_to_window).")]
    public async Task<string> Focus(
        [Description("Window title to focus")] string title)
    {
        bool ok = await _window.SwitchToAsync(title);
        return ok ? $"focused '{title}'" : $"window '{title}' not found";
    }

    [McpServerTool, Description("Enumerate all connected monitors and return geometry information.")]
    public async Task<string> MultiMonitor()
    {
        var monitors = await _window.EnumerateMonitorsAsync();
        return JsonSerializer.Serialize(monitors);
    }
}
