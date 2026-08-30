using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class NotificationService : INotificationService
{
    private readonly IPowerShellService _ps;

    public NotificationService(IPowerShellService ps) => _ps = ps;

    public async Task ShowAsync(string title, string message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var safeTitle = EscapeXml(title);
        var safeMessage = EscapeXml(message);

        var script = $@"
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('<toast><visual><binding template=""ToastGeneric""><text>{safeTitle}</text><text>{safeMessage}</text></binding></visual></toast>')
$notif = [Windows.UI.Notifications.ToastNotification]::new($xml)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Windows-MCP')
$notifier.Show($notif)
";
        await _ps.RunAsync(script, ct);
    }

    internal static string EscapeXml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
