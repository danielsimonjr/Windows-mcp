using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;

namespace WindowsMcp;

internal static class Program
{
    /// <summary>
    /// The version this server reports over MCP, taken from &lt;Version&gt; in Directory.Build.props.
    /// Never hardcode it: the previous literal silently rotted for three releases (stuck at "0.4.1"
    /// while 0.5.0 and 0.6.0 shipped), and a server that misreports its own version is exactly what
    /// makes a stale-bundle deploy invisible. Pinned to plugin.json by ServerInfoTests.
    /// </summary>
    internal static string ServerVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        // Register AppUserModelID first so WinRT ToastNotification works.
        try { PInvoke.SetCurrentProcessExplicitAppUserModelID("org.windows-mcp.server"); }
        catch { /* best effort */ }

        // Per-monitor DPI awareness V2: screen geometry and screenshots use physical pixels.
        // Required for correct HiDPI behavior across multi-monitor setups.
        // Must be called before any window/screen API. Affects ScreenshotService default
        // region AND InputService coordinate normalization (both call GetSystemMetrics).
        // CsWin32 exposes DPI_AWARENESS_CONTEXT as a HANDLE-typed struct, not an enum;
        // the documented sentinel values (per Win32 docs / windef.h) are negative
        // integers cast to the handle type. -4 == PER_MONITOR_AWARE_V2.
        try
        {
            PInvoke.SetProcessDpiAwarenessContext(
                new DPI_AWARENESS_CONTEXT((nint)(-4)));
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 10 1703: fall back to per-monitor V1.
            PInvoke.SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
        }

        // CRITICAL: MCP stdio servers must log to stderr only. stdout is JSON-RPC.
        // CRITICAL: On Windows, Console.Out defaults to the system codepage (cp1252).
        // The MCP SDK's StdioServerTransport calls Console.OpenStandardOutput() at DI
        // resolution time and wraps it with Console.Out's current encoding. If the
        // encoding is not UTF-8, the underlying StreamWriter uses a BOM-less cp1252
        // writer — but more importantly, the raw Stream returned by
        // Console.OpenStandardOutput() is a synchronous ConsoleStream that only flushes
        // when AutoFlush is true on the TextWriter layer. When the encoding is not
        // explicitly set to UTF-8 before host startup, Console.Out's StreamWriter has
        // AutoFlush=false on Windows, causing all JSON-RPC responses to be buffered
        // internally and never flushed to the pipe before the process exits.
        // Fix: set both encodings to UTF-8 (no BOM) before the host/DI starts.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Register all services as singletons.
        builder.Services.AddSingleton<IInputService, InputService>();
        builder.Services.AddSingleton<IScreenshotService, ScreenshotService>();
        builder.Services.AddSingleton<IOcrService, OcrService>();
        builder.Services.AddSingleton<IClipboardService, ClipboardService>();
        builder.Services.AddSingleton<IAudioService, AudioService>();
        builder.Services.AddSingleton<IPowerShellService, PowerShellService>();
        builder.Services.AddSingleton<IUIAutomationService, UIAutomationService>();
        builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
        builder.Services.AddSingleton<IRegistryService, RegistryService>();
        builder.Services.AddSingleton<IServiceControlService, ServiceControlService>();
        builder.Services.AddSingleton<IEventLogService, EventLogService>();
        builder.Services.AddSingleton<ITaskSchedulerService, TaskSchedulerService>();
        builder.Services.AddSingleton<IProcessService, ProcessService>();
        builder.Services.AddSingleton<IWindowService, WindowService>();
        builder.Services.AddSingleton<IWmiService, WmiService>();
        builder.Services.AddSingleton<IStorageService, StorageService>();
        builder.Services.AddSingleton<IDiskService, DiskService>();
        builder.Services.AddSingleton<ISecurityService, SecurityService>();
        builder.Services.AddSingleton<IFirewallService, FirewallService>();
        builder.Services.AddSingleton<ICertStoreService, CertStoreService>();
        builder.Services.AddSingleton<IReliabilityService, ReliabilityService>();
        builder.Services.AddSingleton<IDriverService, DriverService>();
        builder.Services.AddSingleton<IFileStreamService, FileStreamService>();
        builder.Services.AddSingleton<IEnvService, EnvService>();
        builder.Services.AddSingleton<IPowerService, PowerService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<INetworkService, NetworkService>();
        builder.Services.AddSingleton<IWebService, WebService>();
        builder.Services.AddSingleton<IAuthenticodeInspector, AuthenticodeInspector>();
        builder.Services.AddSingleton<ILspEnumerator, LspEnumerator>();
        builder.Services.AddSingleton<IShortcutResolver, ShortcutResolver>();
        builder.Services.AddSingleton<IStartupReportService, StartupReportService>();

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "Windows-mcp", Version = ServerVersion };
            })
            // Surface our deliberate refusals verbatim. Without this the SDK flattens every
            // non-McpException to "An error occurred invoking '<tool>'.", so the PID-reuse guard
            // aborting a kill looks identical to the tool crashing — and a caller may "retry"
            // without the guard, causing exactly the kill the guard existed to prevent.
            // Unexpected faults still fall through to the SDK's masking. See ToolErrors.
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (ctx, ct) =>
            {
                try
                {
                    return await next(ctx, ct);
                }
                catch (Exception ex) when (ToolErrors.IsCallerFacing(ex))
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }],
                    };
                }
            }))
            .WithStdioServerTransport()
            .WithToolsFromAssembly();   // source generator discovers [McpServerTool] methods

        await builder.Build().RunAsync();
        return 0;
    }
}
