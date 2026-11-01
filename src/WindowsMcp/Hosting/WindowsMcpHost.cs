using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using ModelContextProtocol.AspNetCore;

namespace WindowsMcp.Hosting;

/// <summary>
/// Everything both transports share — service registration, server identity, the caller-facing
/// error filter, tool discovery — plus the HTTP host factory. Keeping the shared wiring in one
/// place is what stops the stdio and HTTP modes from drifting apart.
/// </summary>
internal static class WindowsMcpHost
{
    /// <summary>Route the Streamable HTTP transport is mapped at; clients connect to <c>scheme://host:port/mcp</c>.</summary>
    public const string McpPath = "/mcp";

    /// <summary>
    /// Logs go to stderr in <b>both</b> modes. In stdio mode stdout is the JSON-RPC channel, so
    /// anything else there corrupts the protocol; in HTTP mode keeping the same sink means one
    /// logging story, and it also sidesteps the Windows EventLog provider that
    /// <see cref="WebApplication.CreateBuilder()"/> would otherwise add. Every entry opens with
    /// the local date and time to the millisecond, so a stderr line can be tied to when it happened.
    /// </summary>
    public static void ConfigureStderrLogging(ILoggingBuilder logging, bool http)
    {
        logging.ClearProviders();
        logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // Names the formatter as well as configuring it: with no FormatterName the provider
        // overwrites the simple formatter's options and the timestamp never renders. Naming it
        // also makes Logging:Console:FormatterOptions live (env vars, a working-directory
        // appsettings.json), so every field that changes the line's content or layout is pinned
        // here, after config binding; ColorBehavior stays configurable (cosmetic, stderr only).
        logging.AddSimpleConsole(o =>
        {
            // Quoted ':' is a literal. An unquoted ':' is the culture's time separator
            // (fi-FI prints '.'), so the stamp must quote both colons to read the same
            // on every machine — the 0.7.7 fix from main, kept on this host method.
            o.TimestampFormat = "yyyy-MM-dd HH':'mm':'ss.fff ";
            o.UseUtcTimestamp = false;
            o.SingleLine = false;
            o.IncludeScopes = false;
        });
        logging.SetMinimumLevel(LogLevel.Information);

        if (http)
        {
            // Stateless Streamable HTTP builds a fresh McpServer per request, so the SDK's
            // per-server lifecycle chatter would otherwise repeat for every single call.
            logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        }
    }

    /// <summary>
    /// Registers every service singleton and the MCP server (identity, error filter, tools).
    /// The caller appends the transport: <c>.WithStdioServerTransport()</c> or <c>.WithHttpTransport(...)</c>.
    /// </summary>
    public static IMcpServerBuilder AddWindowsMcp(this IHostApplicationBuilder builder, ServerOptions options)
    {
        var services = builder.Services;

        // Process-level knobs the tools read (roadmap C7): ServerOptions is internal to this
        // assembly, so the public options record is what crosses into the tool layer.
        services.AddSingleton(new ScreenshotOptions(options.ScreenshotScale, options.Flash, options.ProfileSnapshot, options.ScreenshotBackend));
        services.AddSingleton(new UiTreeOptions(options.MaxTreeElements, options.ProfileSnapshot));
        // C-5: the HTTP transport is stateless (BuildHttpApp), so sampling cannot reach the client there.
        services.AddSingleton(new TransportOptions(Stateless: options.Transport == TransportKind.Http));
        // Always registered: the tool gates on ScreenshotOptions.Flash, and the overlay's thread
        // only starts on its first Show, so an off switch costs nothing.
        services.AddSingleton<IFlashOverlay, FlashOverlay>();

        services.AddSingleton<IInputService, InputService>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();
        services.AddSingleton<IOcrService, OcrService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IPowerShellService, PowerShellService>();
        services.AddSingleton<IUIAutomationService, UIAutomationService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IRegistryService, RegistryService>();
        services.AddSingleton<IServiceControlService, ServiceControlService>();
        services.AddSingleton<IEventLogService, EventLogService>();
        services.AddSingleton<ITaskSchedulerService, TaskSchedulerService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IVirtualDesktopService, VirtualDesktopService>();
        services.AddSingleton<IAppCatalogService, AppCatalogService>();   // B-8: the launch catalog
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IWmiService, WmiService>();
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<IDiskService, DiskService>();
        services.AddSingleton<ISecurityService, SecurityService>();
        services.AddSingleton<IFirewallService, FirewallService>();
        services.AddSingleton<ICertStoreService, CertStoreService>();
        services.AddSingleton<IReliabilityService, ReliabilityService>();
        services.AddSingleton<IDriverService, DriverService>();
        services.AddSingleton<IFileStreamService, FileStreamService>();
        services.AddSingleton<IEnvService, EnvService>();
        services.AddSingleton<IPowerService, PowerService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<IWebService, WebService>();
        services.AddSingleton<IAuthenticodeInspector, AuthenticodeInspector>();
        services.AddSingleton<ILspEnumerator, LspEnumerator>();
        services.AddSingleton<IShortcutResolver, ShortcutResolver>();
        services.AddSingleton<IStartupReportService, StartupReportService>();
        services.AddSingleton<IIntegrityService, IntegrityService>();
        services.AddSingleton<IUsnService, UsnService>();
        services.AddSingleton<IWatchService, WatchService>();
        services.AddSingleton<IJobService, JobService>();

        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "Windows-mcp", Version = Program.ServerVersion };
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
                        Content = [new TextContentBlock { Text = ToolErrors.MessageFor(ex) }],
                    };
                }
            }))
            // Explicit assembly: the calling-assembly default would pick the wrong one if this
            // ever moved, and the test host builds the HTTP app from another assembly.
            .WithToolsFromAssembly(typeof(WindowsMcpHost).Assembly);
    }

    /// <summary>
    /// Builds (does not run) the HTTP host: Kestrel on <c>bind:port</c>, HTTP/1.1 only, TLS when
    /// <paramref name="cert"/> is given, the bearer gate when an API key is configured, and the
    /// stateless Streamable HTTP transport at <see cref="McpPath"/>. Separated from
    /// <see cref="Program"/> so tests can start it in-process on an ephemeral port.
    /// </summary>
    /// <param name="configureServices">
    /// Runs after <see cref="AddWindowsMcp"/>, so a registration made here wins — the seam the
    /// transport tests use to swap a service (e.g. the screen capture) for a fake.
    /// </param>
    public static WebApplication BuildHttpApp(
        ServerOptions options, X509Certificate2? cert, Action<IServiceCollection>? configureServices = null)
    {
        if (!options.IsHttp)
            throw new ArgumentException("BuildHttpApp requires TransportKind.Http.", nameof(options));
        if (options.UseTls && cert is null)
            throw new ArgumentException("A certificate thumbprint is configured but no certificate was supplied.", nameof(cert));

        // No args: our parser owns the command line. CreateBuilder (not CreateSlimBuilder) so the
        // full Kestrel HTTPS plumbing is present.
        var builder = WebApplication.CreateBuilder();
        ConfigureStderrLogging(builder.Logging, http: true);

        builder.AddWindowsMcp(options)
            .WithHttpTransport(o =>
            {
                // Stateless: no server->client requests are used by any tool (no sampling,
                // elicitation, or unsolicited notifications — progress still rides the POST's
                // SSE stream), and a server restart stays invisible to the client instead of
                // 404-ing a stale Mcp-Session-Id.
                o.Stateless = true;
            });
        configureServices?.Invoke(builder.Services);

        var bind = IPAddress.Parse(options.BindAddress);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Explicit Listen() overrides ASPNETCORE_URLS / --urls, so the environment can't
            // silently add an unauthenticated second endpoint.
            kestrel.Listen(bind, options.Port, listen =>
            {
                // Claude Code's client is HTTP/1.1; pinning avoids ALPN/HTTP2 edge cases on older hosts.
                listen.Protocols = HttpProtocols.Http1;
                if (cert is not null)
                    listen.UseHttps(cert);   // HTTPS only — plaintext on this port fails the TLS handshake.
            });
        });

        var app = builder.Build();

        // Before MapMcp so the gate sits between the auto-inserted UseRouting and UseEndpoints:
        // every path, matched or not, is covered — no unauthenticated probe surface.
        if (options.ApiKey is { } apiKey)
            app.Use(BearerGate(apiKey));

        app.MapMcp(McpPath);

        app.Lifetime.ApplicationStarted.Register(() =>
            app.Logger.LogInformation(
                "Windows-mcp {Version} listening at {Address}{Path} (auth: {Auth}, tls: {Tls})",
                Program.ServerVersion,
                GetListeningAddress(app) ?? $"{options.Scheme}://{options.BindAddress}:{options.Port}",
                McpPath,
                options.ApiKey is null ? "none" : "bearer",
                cert is null ? "off" : $"{cert.Subject} [{cert.Thumbprint}]"));

        return app;
    }

    /// <summary>
    /// The address Kestrel actually bound (port 0 resolved), e.g. <c>http://127.0.0.1:54321</c>.
    /// Only populated once the server has started. <c>app.Urls</c> does not reflect <c>Listen()</c> endpoints.
    /// </summary>
    public static string? GetListeningAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();

    /// <summary>
    /// True when <paramref name="authorizationHeader"/> is <c>Bearer &lt;key&gt;</c> for the expected key.
    /// Constant-time comparison so the key can't be recovered byte-by-byte from response timing.
    /// </summary>
    public static bool IsAuthorized(string? authorizationHeader, ReadOnlySpan<byte> expectedKey)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[prefix.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, expectedKey);
    }

    private static Func<HttpContext, RequestDelegate, Task> BearerGate(string apiKey)
    {
        var expected = Encoding.UTF8.GetBytes(apiKey);
        return async (ctx, next) =>
        {
            if (IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected))
            {
                await next(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ctx.Response.WriteAsync("Unauthorized: send 'Authorization: Bearer <api-key>'.", ctx.RequestAborted);
        };
    }
}
