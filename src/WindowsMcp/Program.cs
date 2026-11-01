using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;
using WindowsMcp.Hosting;

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

    /// <summary>
    /// The one logging setup both transports share, forwarded to
    /// <see cref="WindowsMcpHost.ConfigureStderrLogging"/>. Main's 0.7.7
    /// <c>LoggingTests</c> call this name; stdio and HTTP both go through the host method.
    /// </summary>
    internal static void ConfigureLogging(Microsoft.Extensions.Logging.ILoggingBuilder logging)
        => WindowsMcpHost.ConfigureStderrLogging(logging, http: false);

    public static async Task<int> Main(string[] args)
    {
        // Repair the environment before ANYTHING spawns a child: Claude Desktop launches stdio
        // servers with PATHEXT=.CPL and ~18 variables, which breaks every exe lookup in the
        // powershell tool and crashes `docker mcp` (no ProgramData). See EnvironmentRepair.
        var repaired = EnvironmentRepair.Apply();
        if (repaired.Count > 0)
            Console.Error.WriteLine($"Windows-mcp: repaired environment ({string.Join(", ", repaired)})");

        // The process AppUserModelID groups our windows on the taskbar. Toasts do not depend on
        // it since C-4: NotificationService names its own id (registered under HKCU) per call.
        try { PInvoke.SetCurrentProcessExplicitAppUserModelID("org.windows-mcp.server"); }
        catch { /* best effort */ }

        // Per-monitor DPI awareness V2: screen geometry and screenshots use physical pixels.
        // Required for correct HiDPI behavior across multi-monitor setups.
        // Must be called before any window/screen API. It keeps ScreenshotService's default
        // region, InputService's SetCursorPos placement, UIA bounding rectangles and multi_monitor
        // in ONE physical-pixel coordinate space (virtual desktop, origin = primary's top-left).
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

        ServerOptions options;
        try
        {
            options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
        }
        catch (OptionsException ex)
        {
            Console.Error.WriteLine($"Windows-mcp: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(ServerOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(ServerOptions.Usage);
            return 0;
        }

        return options.IsHttp
            ? await RunHttpAsync(options)
            : await RunStdioAsync(args, options);
    }

    /// <summary>The default: JSON-RPC over stdin/stdout, as launched by the plugin's <c>.mcp.json</c>.</summary>
    private static async Task<int> RunStdioAsync(string[] args, ServerOptions options)
    {
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
        WindowsMcpHost.ConfigureStderrLogging(builder.Logging, http: false);

        builder.AddWindowsMcp(options)
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>Streamable HTTP(S) on a TCP port for remote clients; see <see cref="WindowsMcpHost.BuildHttpApp"/>.</summary>
    private static async Task<int> RunHttpAsync(ServerOptions options)
    {
        // UTF-8 so non-ASCII log text renders; not protocol-critical here (stdout is not the
        // transport). Without an attached console — Task Scheduler, a detached launch — the
        // setters throw, and that must not stop the server.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException) { /* no console attached */ }

        if (!options.IsLoopback && options.ApiKey is null)
        {
            Console.Error.WriteLine(
                $"Windows-mcp: refusing to listen on {options.BindAddress}:{options.Port} without an API key — " +
                "every tool (powershell, file_write, registry_set, process kill, ...) would be open to the network. " +
                $"Set {ServerOptions.EnvPrefix}API_KEY (or pass --api-key), or restrict to this machine with --bind 127.0.0.1.");
            return 2;
        }

        X509Certificate2? cert = null;
        if (options.CertThumbprint is { } thumbprint)
        {
            try
            {
                cert = CertificateLocator.Find(thumbprint);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Windows-mcp: {ex.Message}");
                return 2;
            }
        }
        else if (!options.IsLoopback)
        {
            Console.Error.WriteLine(
                $"Windows-mcp: WARNING: listening on {options.BindAddress}:{options.Port} over plain HTTP — " +
                "the API key and all tool traffic cross the network unencrypted. Pass --cert-thumbprint to serve HTTPS.");
        }

        var app = WindowsMcpHost.BuildHttpApp(options, cert);
        await app.RunAsync();
        return 0;
    }
}
