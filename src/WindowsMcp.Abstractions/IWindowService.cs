using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IWindowService
{
    Task<WindowAction> ExecuteAsync(string action, string? title, CancellationToken ct = default);
    Task<WindowFocusResult> SwitchToAsync(string title, CancellationToken ct = default);
    Task<int> LaunchAsync(string appName, CancellationToken ct = default);
    Task<MonitorInfo[]> EnumerateMonitorsAsync(CancellationToken ct = default);
}
