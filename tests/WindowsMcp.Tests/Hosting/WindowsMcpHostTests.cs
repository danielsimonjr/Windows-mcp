using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;
using WindowsMcp.Services;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// A-9 (R6): the process-level screenshot scale is parsed once by <see cref="ServerOptions"/> and
/// handed to the services as a record (roadmap C7 — no <c>Environment.GetEnvironmentVariable</c>
/// inside a service). Both transports go through <c>AddWindowsMcp</c>, so registering it there is
/// what makes the option apply to stdio and HTTP alike.
/// </summary>
[Trait("Category", "Unit")]
public class WindowsMcpHostTests
{
    private static ServiceProvider Build(ServerOptions options)
    {
        // Empty builder: no configuration providers, no environment scan — AddWindowsMcp only
        // ever touches builder.Services, and nothing here is resolved except the options record.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddWindowsMcp(options);
        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    public void AddWindowsMcp_registers_the_screenshot_scale_from_the_server_options()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotScale = 0.5 });

        provider.GetRequiredService<ScreenshotOptions>().Scale.Should().Be(0.5);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_default_scale_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<ScreenshotOptions>().Scale.Should().Be(1.0);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_screenshot_options_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotScale = 0.25 });

        provider.GetRequiredService<ScreenshotOptions>()
            .Should().BeSameAs(provider.GetRequiredService<ScreenshotOptions>());
    }

    [Fact]
    public void ScreenshotOptions_Default_is_no_scaling()
    {
        ScreenshotOptions.Default.Scale.Should().Be(1.0);
    }

    // ---- BuildHttpApp argument guards ------------------------------------------------------
    // A-9 changed this method's signature (the configureServices seam). Its two guards run
    // before Kestrel is touched, so they are unit-testable and must stay that way: a misrouted
    // stdio options record, or a TLS thumbprint with no certificate, must fail loudly at build
    // time rather than start an endpoint that is not the one the operator asked for.

    [Fact]
    public void BuildHttpApp_rejects_a_stdio_options_record()
    {
        var act = () => WindowsMcpHost.BuildHttpApp(ServerOptions.Stdio, cert: null);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("options");
        ex.Message.Should().Contain("Http");
    }

    [Fact]
    public void BuildHttpApp_rejects_a_configured_thumbprint_with_no_certificate()
    {
        var options = new ServerOptions(
            TransportKind.Http, "127.0.0.1", 0,
            CertThumbprint: "0123456789ABCDEF0123456789ABCDEF01234567", ApiKey: "a-key-that-is-long-enough");

        var act = () => WindowsMcpHost.BuildHttpApp(options, cert: null);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("cert");
        ex.Message.Should().Contain("certificate");
    }

    // ---- A-2 / A-4 (R1): the element budget crosses into the service layer the same way ------
    // ServerOptions is internal to the server assembly, so UiTreeOptions is the public record the
    // sealed UIAutomationService is constructed with (roadmap C7 — no Environment.GetEnvironmentVariable
    // inside a service). Registered in AddWindowsMcp, so stdio and HTTP get it alike.

    [Fact]
    public void AddWindowsMcp_registers_the_tree_budget_from_the_server_options()
    {
        using var provider = Build(ServerOptions.Stdio with { MaxTreeElements = 200 });

        provider.GetRequiredService<UiTreeOptions>().MaxElements.Should().Be(200);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_default_tree_budget_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<UiTreeOptions>().MaxElements.Should().Be(500);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_tree_options_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio with { MaxTreeElements = 42 });

        provider.GetRequiredService<UiTreeOptions>()
            .Should().BeSameAs(provider.GetRequiredService<UiTreeOptions>());
    }

    // ---- A-14 (R1): the flash switch, the profiling switch, and the overlay singleton ---------
    // Both switches ride the SAME two options records the earlier items introduced (roadmap C7),
    // so nothing new crosses into the tool layer except the overlay itself. The overlay is
    // registered unconditionally: the TOOL gates on ScreenshotOptions.Flash, so a server started
    // with --flash off still resolves ScreenTools.

    [Fact]
    public void AddWindowsMcp_registers_the_flash_switch_from_the_server_options()
    {
        using var on = Build(ServerOptions.Stdio);
        using var off = Build(ServerOptions.Stdio with { Flash = false });

        on.GetRequiredService<ScreenshotOptions>().Flash.Should().BeTrue("the flash is on by default");
        off.GetRequiredService<ScreenshotOptions>().Flash.Should().BeFalse("--flash off must reach the tool layer");
    }

    [Fact]
    public void AddWindowsMcp_registers_the_profiling_switch_on_both_options_records()
    {
        using var provider = Build(ServerOptions.Stdio with { ProfileSnapshot = true });

        provider.GetRequiredService<ScreenshotOptions>().Profile
            .Should().BeTrue("--profile-snapshot profiles the capture stages too, not only the walk");
        provider.GetRequiredService<UiTreeOptions>().Profile
            .Should().BeTrue("--profile-snapshot profiles the snapshot walk");
    }

    [Fact]
    public void AddWindowsMcp_leaves_profiling_off_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<ScreenshotOptions>().Profile.Should().BeFalse();
        provider.GetRequiredService<UiTreeOptions>().Profile.Should().BeFalse();
    }

    [Fact]
    public void ScreenshotOptions_Default_flashes_and_does_not_profile()
    {
        ScreenshotOptions.Default.Flash.Should().BeTrue();
        ScreenshotOptions.Default.Profile.Should().BeFalse();
    }

    [Fact]
    public void UiTreeOptions_Default_does_not_profile()
    {
        UiTreeOptions.Default.MaxElements.Should().Be(500);
        UiTreeOptions.Default.Profile.Should().BeFalse();
    }

    [Fact]
    public void AddWindowsMcp_registers_the_flash_overlay_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio);

        var overlay = provider.GetRequiredService<IFlashOverlay>();
        overlay.Should().BeOfType<FlashOverlay>();
        overlay.Should().BeSameAs(provider.GetRequiredService<IFlashOverlay>(),
            "one overlay per process: two would fight over the same screen area");
    }

    // ---- A-12 phase 1 (R4): the virtual-desktop service --------------------------------------
    // WindowTools takes it as a constructor argument, so a missing registration is not a quietly
    // unfilled field — it is a DI failure on every `window` call, on both transports.

    [Fact]
    public void AddWindowsMcp_registers_the_virtual_desktop_service()
    {
        using var provider = Build(ServerOptions.Stdio);

        var desktops = provider.GetRequiredService<IVirtualDesktopService>();
        desktops.Should().BeOfType<VirtualDesktopService>();
        desktops.Should().BeSameAs(provider.GetRequiredService<IVirtualDesktopService>(),
            "one per process, like every other service here");
    }

    // ---- B-8: the app catalog -----------------------------------------------------------------
    // WindowService takes it as a constructor argument, so a missing registration is not a quietly
    // unfilled field - it is every launch(name) failing at the catalog with a null reference.

    [Fact]
    public void AddWindowsMcp_registers_the_app_catalog_service()
    {
        using var provider = Build(ServerOptions.Stdio);

        var catalog = provider.GetRequiredService<IAppCatalogService>();
        catalog.Should().BeOfType<AppCatalogService>();
        catalog.Should().BeSameAs(provider.GetRequiredService<IAppCatalogService>(),
            "one per process - the five-minute cache is worthless if every call gets a new instance");
    }

    [Fact]
    public void AddWindowsMcp_gives_the_window_service_the_catalog_it_was_registered_with()
    {
        // B-8 launches by name through IAppCatalogService; a WindowService built without it would
        // compile, resolve, and then fail on the first launch("calc").
        using var provider = Build(ServerOptions.Stdio);

        var window = provider.GetRequiredService<IWindowService>();

        window.Should().BeOfType<WindowService>();
        var field = typeof(WindowService)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(f => f.FieldType == typeof(IAppCatalogService));
        field.Should().NotBeNull("the service has to hold the catalog it was given");
        field!.GetValue(window).Should().BeSameAs(provider.GetRequiredService<IAppCatalogService>());
    }

    [Fact]
    public void AddWindowsMcp_still_resolves_the_window_service_and_its_tool()
    {
        // WindowService gained an optional IVirtualDesktopService parameter and WindowTools a
        // required one: both must still come out of the container.
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<IWindowService>().Should().BeOfType<WindowService>();
        ActivatorUtilities.CreateInstance<WindowsMcp.Tools.WindowTools>(provider).Should().NotBeNull();
    }

    [Fact]
    public void AddWindowsMcp_registers_the_flash_overlay_even_when_the_flash_is_off()
    {
        // The tool gates on ScreenshotOptions.Flash; the registration is unconditional so that
        // ScreenTools still resolves - a missing registration would fail every screenshot with a
        // DI error instead of quietly not flashing.
        using var provider = Build(ServerOptions.Stdio with { Flash = false });

        provider.GetService<IFlashOverlay>().Should().NotBeNull();
    }
    // ---- C-5 (R8): the transport fact the scrape tool needs -----------------------------------
    // Sampling is a server->client request, so it needs a session. BuildHttpApp runs Streamable
    // HTTP stateless by an earlier decision, and a per-request server has no session; the tool has
    // to be told, because it cannot ask the transport. Registered here so both modes get the truth
    // about themselves from the one place they share.

    [Fact]
    public void AddWindowsMcp_registers_a_session_keeping_transport_for_stdio()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<TransportOptions>().Stateless.Should().BeFalse(
            "stdio keeps one session for the life of the process, so sampling can cross it");
    }

    [Fact]
    public void AddWindowsMcp_registers_a_stateless_transport_for_http()
    {
        using var provider = Build(ServerOptions.Stdio with { Transport = TransportKind.Http });

        provider.GetRequiredService<TransportOptions>().Stateless.Should().BeTrue(
            "BuildHttpApp sets Stateless on the Streamable HTTP transport, so there is no session "
            + "for a sampling reply to come back on");
    }

    [Fact]
    public void AddWindowsMcp_registers_the_transport_options_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<TransportOptions>()
            .Should().BeSameAs(provider.GetRequiredService<TransportOptions>());
    }

    [Fact]
    public void TransportOptions_Stdio_keeps_its_session()
        => TransportOptions.Stdio.Stateless.Should().BeFalse(
            "the default a tool built without an injected record assumes is the session-keeping one");

    /// <summary>
    /// C-5: <c>WebTools</c>'s <c>TransportOptions</c> parameter is optional so the unit tests can
    /// omit it — which means a missing registration would not fail DI, it would silently make every
    /// HTTP <c>summarize:true</c> ask a client that can never answer. This asserts the resolved tool
    /// holds the record the container was configured with.
    /// </summary>
    [Theory]
    [InlineData(false, false)]   // stdio: a session that lasts the process
    [InlineData(true, true)]     // http:  a fresh server per request
    public void AddWindowsMcp_gives_the_web_tool_the_transport_it_registered(bool http, bool stateless)
    {
        using var provider = Build(ServerOptions.Stdio with
        {
            Transport = http ? TransportKind.Http : TransportKind.Stdio,
        });

        var tools = ActivatorUtilities.CreateInstance<WindowsMcp.Tools.WebTools>(provider);

        var field = typeof(WindowsMcp.Tools.WebTools)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(f => f.FieldType == typeof(TransportOptions));
        field.Should().NotBeNull("the tool has to hold the transport it was given, not re-read it per call");
        field!.GetValue(tools).Should().BeSameAs(provider.GetRequiredService<TransportOptions>());
        ((TransportOptions)field.GetValue(tools)!).Stateless.Should().Be(stateless);
    }

    // ---- A-10 (R1/R6): the capture backend crosses into the tool layer, and the service is owned --
    // The backend rides the SAME ScreenshotOptions record the earlier items introduced (roadmap C7).
    // What is new is that ScreenshotService now holds a D3D device, so the container has to be the
    // thing that releases it: a singleton is only disposed if it is IDisposable.

    [Fact]
    public void AddWindowsMcp_registers_the_screenshot_backend_from_the_server_options()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "wgc" });

        provider.GetRequiredService<ScreenshotOptions>().Backend.Should().Be("wgc",
            "--screenshot-backend must reach the service layer, not be re-read from the environment");
    }

    [Fact]
    public void AddWindowsMcp_registers_the_default_backend_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<ScreenshotOptions>().Backend.Should().Be("auto");
    }

    [Fact]
    public void ScreenshotOptions_Default_prefers_the_compositor()
    {
        ScreenshotOptions.Default.Backend.Should().Be("auto");
    }

    [Fact]
    public void AddWindowsMcp_resolves_the_screenshot_service_with_the_process_options()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "gdi" });

        var service = provider.GetRequiredService<IScreenshotService>();

        service.Should().BeOfType<ScreenshotService>();
        service.Should().BeSameAs(provider.GetRequiredService<IScreenshotService>(),
            "one capture service per process: two would each hold their own D3D device");
        service.Should().BeAssignableTo<IDisposable>(
            "the container only disposes a singleton that says it needs disposing");
        provider.GetRequiredService<ScreenshotOptions>().Backend.Should().Be("gdi",
            "and the options the service is constructed with are the ones the command line produced");
    }

    [Fact]
    public void Disposing_the_container_disposes_the_screenshot_service()
    {
        var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "wgc" });
        var service = provider.GetRequiredService<IScreenshotService>();

        var act = provider.Dispose;

        act.Should().NotThrow("shutting the server down releases the capture device, even if it was never created");
        service.Should().NotBeNull();
    }

    /// <summary>
    /// A-10 (R6): the registered record is not the point — the service HONOURING it is. With the
    /// process default at <c>wgc</c>, a call that says <c>auto</c> must refuse when the compositor
    /// cannot serve, instead of quietly taking a GDI frame. If the options never reached the
    /// constructor the resolved backend would be <c>auto</c> and this would fall back to GDI, so
    /// the assertion fails on the exact wiring mistake the record alone cannot catch.
    /// </summary>
    [Fact]
    public async Task The_resolved_screenshot_service_honours_the_configured_backend()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "wgc" });
        var service = (ScreenshotService)provider.GetRequiredService<IScreenshotService>();
        service.WgcFrameSource = _ => null;   // the compositor refuses; no desktop is touched

        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 16, 16), new CaptureOptions());

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("wgc", "--screenshot-backend wgc reached the service that captures");
    }

    /// <summary>A-10 (R6): and the unconfigured server still falls back rather than refusing.</summary>
    [Fact]
    public async Task The_resolved_screenshot_service_falls_back_when_no_backend_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);
        var service = (ScreenshotService)provider.GetRequiredService<IScreenshotService>();
        service.WgcFrameSource = _ => null;

        // 0x0 is the tripwire that proves the GDI frame path was reached without needing a desktop.
        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 0, 0), new CaptureOptions());

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().NotContain("wgc",
            "the default is auto: a refusing compositor is answered with GDI, silently");
    }

    // ---- Log timestamps: every console line carries the local date and time -------------------
    // Both transports take their logging from ConfigureStderrLogging, so what that one method
    // leaves in the container is what stdio and HTTP both print. The target, with the console
    // formatter's two-line layout kept:
    //   2026-10-20 14:03:27.512 info: WindowsMcp.Services.ScreenshotService[0]
    //         capture 1920x1080 in 42 ms

    /// <summary>Date, then time to the millisecond; the trailing space separates it from the level.</summary>
    private const string LogTimestampFormat = "yyyy-MM-dd HH':'mm':'ss.fff ";

    private const string LogCategory = "WindowsMcp.Services.ScreenshotService";
    private const string LogMessage = "capture 1920x1080 in 42 ms";

    private static ServiceProvider BuildLogging(bool http) =>
        new ServiceCollection()
            .AddLogging(logging => WindowsMcpHost.ConfigureStderrLogging(logging, http))
            .BuildServiceProvider();

    [Theory]
    [InlineData(false)]   // stdio
    [InlineData(true)]    // http
    public void ConfigureStderrLogging_stamps_every_line_with_the_local_date_and_time(bool http)
    {
        using var provider = BuildLogging(http);

        var formatter = provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;
        var console = provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;

        // One scope, so a red run names every missing piece at once rather than the first.
        using (new AssertionScope())
        {
            formatter.TimestampFormat.Should().Be(LogTimestampFormat,
                "every line starts with the date and the time to the millisecond, then one space before the level");
            // These two only prove ConfigureStderrLogging does not set the flag itself, in a container
            // with no configuration. Configuration setting it is the *_when_configuration_asks_otherwise rows.
            formatter.UseUtcTimestamp.Should().BeFalse(
                "ConfigureStderrLogging itself must not ask for a UTC stamp");
            formatter.SingleLine.Should().BeFalse(
                "ConfigureStderrLogging itself must not fold the entry onto one line");
            console.FormatterName.Should().Be(ConsoleFormatterNames.Simple,
                "with no formatter name ConsoleLoggerProvider overwrites the simple formatter's options from "
                + "the deprecated ConsoleLoggerOptions fields, and the configured timestamp never renders");
        }
    }

    [Theory]
    [InlineData(false)]   // stdio
    [InlineData(true)]    // http
    public void ConfigureStderrLogging_still_sends_every_level_to_stderr(bool http)
    {
        using var provider = BuildLogging(http);

        provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue
            .LogToStandardErrorThreshold.Should().Be(LogLevel.Trace,
                "stdout is the JSON-RPC channel in stdio mode, so not one log line may land there");
    }

    private const string McpSdkCategory = "ModelContextProtocol.Server.McpServer";

    /// <summary>
    /// The rest of what <c>ConfigureStderrLogging</c> leaves in place: Information and above in
    /// both modes, and the SDK's per-request lifecycle chatter held to Warning over HTTP only
    /// (stateless HTTP builds a server per call; stdio has one server, so its Information stays).
    /// </summary>
    [Theory]
    [InlineData(false, LogCategory, LogLevel.Debug, false)]
    [InlineData(false, LogCategory, LogLevel.Information, true)]
    [InlineData(true, LogCategory, LogLevel.Debug, false)]
    [InlineData(true, LogCategory, LogLevel.Information, true)]
    [InlineData(false, McpSdkCategory, LogLevel.Information, true)]
    [InlineData(true, McpSdkCategory, LogLevel.Information, false)]
    [InlineData(true, McpSdkCategory, LogLevel.Warning, true)]
    public void ConfigureStderrLogging_keeps_information_and_quietens_the_sdk_only_over_http(
        bool http, string category, LogLevel level, bool enabled)
    {
        using var provider = BuildLogging(http);

        provider.GetRequiredService<ILoggerFactory>().CreateLogger(category).IsEnabled(level)
            .Should().Be(enabled, "{0} at {1} over {2}", category, level, http ? "http" : "stdio");
    }

    /// <summary>
    /// The options above are the configuration; this is the line. It renders through the formatter
    /// the console provider actually selected, after the provider has been built — which is when a
    /// formatter left without a name has its options replaced — so it fails on any wiring that
    /// configures a timestamp the console never prints.
    /// </summary>
    [Theory]
    [InlineData(false, LogLevel.Information, "info")]
    [InlineData(true, LogLevel.Information, "info")]
    [InlineData(false, LogLevel.Warning, "warn")]
    [InlineData(true, LogLevel.Error, "fail")]
    public void ConfigureStderrLogging_renders_the_local_timestamp_ahead_of_a_two_line_entry(
        bool http, LogLevel level, string label)
    {
        using var provider = BuildLogging(http);

        var before = DateTime.Now;
        var rendered = RenderThroughTheConsoleProvider(provider, level);
        var after = DateTime.Now;

        AssertTimestampedTwoLineEntry(rendered, label, before, after);
    }

    /// <summary>
    /// The real HTTP host: <c>WebApplication.CreateBuilder</c> has already added its own console,
    /// debug, event-source and EventLog providers and bound <c>Logging:Console</c> from
    /// configuration before <c>ConfigureStderrLogging</c> runs. The line it prints must still carry
    /// the timestamp. Built, never started: no port is opened.
    /// </summary>
    [Fact]
    public async Task BuildHttpApp_stamps_its_console_lines_with_the_local_date_and_time()
    {
        await using var app = WindowsMcpHost.BuildHttpApp(
            new ServerOptions(TransportKind.Http, "127.0.0.1", 0, null, null), cert: null);

        // The flags, not just the clock: on a UTC runner a UTC stamp and a local one are the same text.
        AssertFormatterFlagsPinned(app.Services);

        var before = DateTime.Now;
        var rendered = RenderThroughTheConsoleProvider(app.Services, LogLevel.Information);
        var after = DateTime.Now;

        AssertTimestampedTwoLineEntry(rendered, "info", before, after);
    }

    [Fact]
    public async Task BuildHttpApp_logs_through_the_one_console_provider_only()
    {
        await using var app = WindowsMcpHost.BuildHttpApp(
            new ServerOptions(TransportKind.Http, "127.0.0.1", 0, null, null), cert: null);

        app.Services.GetServices<ILoggerProvider>().Should().ContainSingle(
                "the providers the web builder adds are cleared, and a second console provider would print every line twice")
            .Which.Should().BeOfType<ConsoleLoggerProvider>();
    }

    /// <summary>
    /// The stdio host the way <c>Program.RunStdioAsync</c> composes it: <c>Host.CreateApplicationBuilder</c>
    /// (its own console, debug, event-source and EventLog providers, <c>Logging:Console</c> bound from
    /// configuration), then <c>ConfigureStderrLogging</c>. A mirror of those two lines rather than a
    /// call into them — RunStdioAsync builds and runs the host in one step, so it cannot be built
    /// here without starting the stdio transport. Built, never started.
    /// </summary>
    [Fact]
    public void Stdio_host_builder_stamps_its_console_lines_with_the_local_date_and_time()
    {
        using var host = BuildStdioHost(settings: []);

        // The flags, not just the clock: on a UTC runner a UTC stamp and a local one are the same text.
        AssertFormatterFlagsPinned(host.Services);

        var before = DateTime.Now;
        var rendered = RenderThroughTheConsoleProvider(host.Services, LogLevel.Information);
        var after = DateTime.Now;

        AssertTimestampedTwoLineEntry(rendered, "info", before, after);
    }

    // ---- Configuration must not reshape the line ----------------------------------------------
    // Naming the formatter "simple" is what makes the timestamp render, and it also makes
    // Logging:Console:FormatterOptions:* from configuration take effect (unnamed, the provider
    // overwrote those options, so configuration could not reach them). Both hosts read
    // appsettings.json from the working directory and every environment variable
    // (Logging__Console__FormatterOptions__SingleLine=true), so ConfigureStderrLogging has to win
    // over configuration on every flag that changes the spec: local time and the two-line layout,
    // with no scope line. ColorBehavior stays configurable, because it is cosmetic and writes no
    // stdout bytes.
    //
    // The stdio host gets its configuration from the command line here, which is only a way to
    // feed the default builder: the server's own parser rejects these switches, so in production
    // they arrive through the environment or appsettings.json. BuildHttpApp takes no args, so
    // HTTP is covered by the ServiceCollection rows, which add configuration the way the default
    // builders do: AddConfiguration(Logging) before ConfigureStderrLogging runs.

    /// <summary>Configuration that would change the line's shape: a UTC stamp, one line, a scope line.</summary>
    private static readonly string[] FormatterFlagOverrides =
    [
        "Logging:Console:FormatterOptions:UseUtcTimestamp=true",
        "Logging:Console:FormatterOptions:SingleLine=true",
        "Logging:Console:FormatterOptions:IncludeScopes=true",
    ];

    /// <summary>Configuration that would move the lines to stdout, switch formatter, or replace the stamp.</summary>
    private static readonly string[] SinkOverrides =
    [
        "Logging:Console:LogToStandardErrorThreshold=None",
        "Logging:Console:FormatterName=json",
        "Logging:Console:FormatterOptions:TimestampFormat=X",
    ];

    /// <summary>The two lines of <c>Program.RunStdioAsync</c> that build its host, with <paramref name="settings"/> as configuration.</summary>
    private static IHost BuildStdioHost(string[] settings)
    {
        var builder = Host.CreateApplicationBuilder(args: settings.Select(s => "--" + s).ToArray());
        WindowsMcpHost.ConfigureStderrLogging(builder.Logging, http: false);
        return builder.Build();
    }

    /// <summary>Logging with <paramref name="settings"/> bound first, as a default host builder binds its configuration.</summary>
    private static ServiceProvider BuildLogging(bool http, string[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .Select(s => s.Split('=', 2))
                .Select(kv => new KeyValuePair<string, string?>(kv[0], kv[1])))
            .Build();

        return new ServiceCollection()
            .AddLogging(logging =>
            {
                logging.AddConfiguration(configuration.GetSection("Logging"));
                WindowsMcpHost.ConfigureStderrLogging(logging, http);
            })
            .BuildServiceProvider();
    }

    [Fact]
    public void Stdio_host_builder_keeps_local_time_and_two_lines_when_configuration_asks_otherwise()
    {
        using var host = BuildStdioHost(FormatterFlagOverrides);

        AssertFormatterFlagsPinned(host.Services);

        var before = DateTime.Now;
        var rendered = RenderThroughTheConsoleProvider(host.Services, LogLevel.Information);
        var after = DateTime.Now;

        AssertTimestampedTwoLineEntry(rendered, "info", before, after);
    }

    [Theory]
    [InlineData(false)]   // stdio
    [InlineData(true)]    // http
    public void ConfigureStderrLogging_keeps_local_time_and_two_lines_when_configuration_asks_otherwise(bool http)
    {
        using var provider = BuildLogging(http, FormatterFlagOverrides);

        AssertFormatterFlagsPinned(provider);

        var before = DateTime.Now;
        var rendered = RenderThroughTheConsoleProvider(provider, LogLevel.Information);
        var after = DateTime.Now;

        AssertTimestampedTwoLineEntry(rendered, "info", before, after);
    }

    [Fact]
    public void Stdio_host_builder_keeps_stderr_the_simple_formatter_and_its_stamp_when_configuration_asks_otherwise()
    {
        using var host = BuildStdioHost(SinkOverrides);

        AssertSinkPinned(host.Services);
    }

    [Theory]
    [InlineData(false)]   // stdio
    [InlineData(true)]    // http
    public void ConfigureStderrLogging_keeps_stderr_the_simple_formatter_and_its_stamp_when_configuration_asks_otherwise(bool http)
    {
        using var provider = BuildLogging(http, SinkOverrides);

        AssertSinkPinned(provider);
    }

    /// <summary>
    /// The resolved formatter options, the ones the selected formatter reads. These are checked
    /// directly, not through the clock, so a UTC stamp is caught on a UTC runner too.
    /// </summary>
    private static void AssertFormatterFlagsPinned(IServiceProvider services)
    {
        var formatter = services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;

        using (new AssertionScope())
        {
            formatter.UseUtcTimestamp.Should().BeFalse(
                "the stamp is local time, the clock the operator reads the log against, whatever configuration says");
            formatter.SingleLine.Should().BeFalse(
                "the entry keeps the two-line layout (header, then the message indented below it), whatever configuration says");
            formatter.IncludeScopes.Should().BeFalse(
                "a scope line would sit between the header and the message on every entry inside an HTTP request");
        }
    }

    private static void AssertSinkPinned(IServiceProvider services)
    {
        var console = services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        var formatter = services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;

        using (new AssertionScope())
        {
            console.LogToStandardErrorThreshold.Should().Be(LogLevel.Trace,
                "stdout is the JSON-RPC channel in stdio mode, so configuration must not move a log line there");
            console.FormatterName.Should().Be(ConsoleFormatterNames.Simple,
                "the timestamped two-line layout is the simple formatter's; configuration must not switch it");
            formatter.TimestampFormat.Should().Be(LogTimestampFormat,
                "the stamp's format is the spec's, not configuration's");
        }
    }

    /// <summary>
    /// Renders one entry the way the server's console logger would: the provider is built first
    /// (resolving the factory constructs it, and creating a logger is the path every service
    /// takes), then the formatter is picked the way <see cref="ConsoleLoggerProvider"/> picks it —
    /// by <see cref="ConsoleLoggerOptions.FormatterName"/>, falling back to the simple one. The
    /// culture is pinned because the formatter renders the timestamp in the current culture (a
    /// custom format's ':' is that culture's time separator, 'yyyy' its calendar's year), and the
    /// ANSI colour codes the formatter emits on an unredirected console are stripped. The entry is
    /// written inside an open scope, as every entry logged during an HTTP request is, so a formatter
    /// left including scopes shows it as an extra line.
    /// </summary>
    private static string RenderThroughTheConsoleProvider(IServiceProvider services, LogLevel level)
    {
        _ = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

        var name = services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName;
        var formatters = services.GetServices<ConsoleFormatter>().ToList();
        var formatter = formatters.SingleOrDefault(f => f.Name == name)
            ?? formatters.Single(f => f.Name == ConsoleFormatterNames.Simple);

        var entry = new LogEntry<string>(level, LogCategory, new EventId(0), LogMessage, exception: null,
            (state, _) => state);

        var scopes = new LoggerExternalScopeProvider();
        using var requestScope = scopes.Push("RequestId:0HN7TIMESTAMPS");

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            formatter.Write(in entry, scopes, writer);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        return Regex.Replace(writer.ToString(), @"\x1B\[[0-9;]*m", string.Empty);
    }

    private static void AssertTimestampedTwoLineEntry(string rendered, string label, DateTime before, DateTime after)
    {
        var lines = rendered.Split(["\r\n", "\n"], StringSplitOptions.None);

        lines[0].Should().MatchRegex(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} " + Regex.Escape(label) + @": "
                + Regex.Escape(LogCategory) + @"\[0\]$",
            "the header line opens with the date and time to the millisecond, one space, then the level");
        lines.Should().HaveCountGreaterThan(1, "the message goes on its own line");
        lines[1].Should().Be("      " + LogMessage,
            "the two-line layout puts the message on the second line, indented under the header");
        lines.Skip(2).Should().AllSatisfy(l => l.Should().BeEmpty(), "one entry is two lines, nothing more");

        var stamp = DateTime.ParseExact(lines[0][..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        stamp.Should().BeOnOrAfter(before.AddSeconds(-1),
                "the stamp is the local clock at the time of the write (a UTC stamp is off by the zone offset)")
            .And.BeOnOrBefore(after.AddSeconds(1));
    }
}
