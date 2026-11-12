using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Moq;
using ModelContextProtocol.Protocol;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;
using WindowsMcp.Tests.Fixtures;

namespace WindowsMcp.Tests.Hosting;

// MCP9005: the SDK marks the Sampling feature obsolete as of specification version 2026-07-28
// (SEP-2577). C-5's summary path exists only through client-side sampling, and this file holds
// the only test that drives the real one; see src/WindowsMcp/Tools/ISamplingClient.cs.
#pragma warning disable MCP9005

/// <summary>
/// Starts the real HTTP host (<see cref="WindowsMcpHost.BuildHttpApp"/>) in-process on an
/// ephemeral loopback port and talks to it with the SDK's own client. This is the only test that
/// exercises the service/tool wiring both transports share end-to-end — nothing drives the stdio
/// host in-process.
/// </summary>
[Trait("Category", "Integration")]
public class HttpTransportTests
{
    private const string ApiKey = "integration-test-api-key-0123";

    // ---- harness --------------------------------------------------------------------------

    internal sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public Uri BaseAddress { get; }
        public Uri McpEndpoint => new(BaseAddress, WindowsMcpHost.McpPath);

        private Harness(WebApplication app, Uri baseAddress)
        {
            App = app;
            BaseAddress = baseAddress;
        }

        /// <param name="configureServices">
        /// Runs after <c>AddWindowsMcp</c>, so a registration here replaces the real service
        /// (A-7 follow-up: without this seam every transport test of the screenshot surface has
        /// to capture the real screen, which no headless run can do).
        /// </param>
        public static async Task<Harness> StartAsync(
            string? apiKey = null,
            X509Certificate2? cert = null,
            Action<IServiceCollection>? configureServices = null)
        {
            // Port 0: Kestrel picks a free port and reports it via IServerAddressesFeature —
            // no pick-then-bind race. S-11 (D1): the control indicator is OFF here. It is on by
            // default under both transports, so without this every test that calls a driving tool
            // (switch_to_window's miss, multi_select's refusal, ...) would draw a ring and a banner
            // on the desktop and register the user's real Ctrl+Alt+Shift+Backspace. The filter
            // tests swap in a mock IControlIndicator through configureServices, and the
            // real-indicator pipeline test re-enables it with a test-only chord the same way.
            var options = new ServerOptions(TransportKind.Http, "127.0.0.1", 0, cert?.Thumbprint, apiKey, ControlIndicator: false);
            var app = WindowsMcpHost.BuildHttpApp(options, cert, configureServices);
            await app.StartAsync();

            var address = WindowsMcpHost.GetListeningAddress(app)
                ?? throw new InvalidOperationException("Kestrel reported no listening address");
            return new Harness(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    internal static async Task<McpClient> ConnectAsync(Uri endpoint, string? apiKey = null, HttpClient? httpClient = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,   // pin: AutoDetect would mask a Streamable HTTP regression by falling back to SSE
            AdditionalHeaders = apiKey is null
                ? null
                : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        };

        var transport = httpClient is null
            ? new HttpClientTransport(options)
            : new HttpClientTransport(options, httpClient);

        return await McpClient.CreateAsync(transport);
    }

    private static StringContent InitializeBody() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""",
        Encoding.UTF8,
        "application/json");

    private static string FileWriteToolName(IEnumerable<McpClientTool> tools) =>
        tools.Single(t => t.Name.Replace("_", "").Equals("filewrite", StringComparison.OrdinalIgnoreCase)).Name;

    internal static string ScreenshotToolName(IEnumerable<McpClientTool> tools) =>
        tools.Single(t => t.Name.Replace("_", "").Equals("screenshot", StringComparison.OrdinalIgnoreCase)).Name;

    internal static string SnapshotToolName(IEnumerable<McpClientTool> tools) =>
        tools.Single(t => t.Name.Replace("_", "").Equals("snapshot", StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>
    /// A throwaway localhost server certificate. SChannel cannot serve TLS with the purely
    /// in-memory key CreateSelfSigned produces, so it is round-tripped through PKCS#12.
    /// </summary>
    internal static X509Certificate2 CreateEphemeralServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }

    // ---- tests ----------------------------------------------------------------------------

    [Fact]
    public async Task Handshake_and_tool_listing_work_over_plain_http()
    {
        await using var server = await Harness.StartAsync();
        server.BaseAddress.Scheme.Should().Be("http");

        await using var client = await ConnectAsync(server.McpEndpoint);

        client.ServerInfo.Name.Should().Be("Windows-mcp");
        client.ServerInfo.Version.Should().Be(Program.ServerVersion,
            "the HTTP host must report the same identity the stdio host does");

        var tools = await client.ListToolsAsync();
        tools.Should().HaveCountGreaterThanOrEqualTo(60);
        FileWriteToolName(tools).Should().NotBeNullOrEmpty();
    }

    // ---- E-1: server instructions in the handshake ---------------------------------------------

    /// <summary>
    /// E-1 (R1): a client that never installed the plugin skill is told the loop at connect time.
    /// The SDK client exposes what the server sent in <c>initialize</c> as
    /// <see cref="McpClient.ServerInstructions"/>; it must be exactly the constant, so nothing in
    /// the HTTP wiring rewrites, trims or drops it.
    /// </summary>
    [Fact]
    public async Task Initialize_over_http_carries_the_server_instructions()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        client.ServerInstructions.Should().NotBeNullOrEmpty(
            "AddWindowsMcp sets the instructions, so the HTTP transport sends them in initialize");
        client.ServerInstructions.Should().Be(ServerInstructions.Text);
    }

    /// <summary>
    /// E-1 (R1), below the SDK client: the protocol field a non-.NET client reads is
    /// <c>result.instructions</c> of the raw JSON-RPC <c>initialize</c> response. Stateless
    /// Streamable HTTP may answer as a JSON body or as one SSE <c>data:</c> frame; both are read.
    /// </summary>
    [Fact]
    public async Task Raw_initialize_result_carries_the_instructions_field()
    {
        await using var server = await Harness.StartAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.McpEndpoint) { Content = InitializeBody() };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(JsonRpcPayload(body, response.Content.Headers.ContentType?.MediaType));
        var result = doc.RootElement.GetProperty("result");
        result.TryGetProperty("instructions", out var instructions).Should().BeTrue(
            "the initialize result carries the instructions field; the server sent: {0}", body);
        instructions.GetString().Should().NotBeNullOrEmpty().And.Be(ServerInstructions.Text);
    }

    /// <summary>The JSON-RPC message in a Streamable HTTP response body: the body itself, or the SSE <c>data:</c> lines.</summary>
    private static string JsonRpcPayload(string body, string? mediaType)
    {
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return body;

        var data = body.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].TrimStart())
            .ToArray();
        data.Should().NotBeEmpty("an SSE answer to initialize carries the response in a data: frame");
        return string.Join("\n", data);
    }

    [Fact]
    public async Task Api_key_gates_every_path_not_just_the_mcp_route()
    {
        await using var server = await Harness.StartAsync(apiKey: ApiKey);
        using var http = new HttpClient();

        foreach (var path in new[] { WindowsMcpHost.McpPath, "/", "/anything-else" })
        {
            using var response = await http.PostAsync(new Uri(server.BaseAddress, path), InitializeBody());

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"no credentials were sent to {path}");
            response.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, server.McpEndpoint) { Content = InitializeBody() })
        {
            request.Headers.Authorization = new("Bearer", ApiKey + "x");
            using var response = await http.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a wrong key must be refused");
        }

        // The right key, sent the way Claude Code sends it (a static header) -> full handshake.
        await using var client = await ConnectAsync(server.McpEndpoint, ApiKey);
        client.ServerInfo.Name.Should().Be("Windows-mcp");
    }

    [Fact]
    public async Task Certificate_makes_the_port_https_only()
    {
        using var cert = CreateEphemeralServerCertificate();
        await using var server = await Harness.StartAsync(cert: cert);
        server.BaseAddress.Scheme.Should().Be("https");

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var httpsClient = new HttpClient(handler);
        await using var client = await ConnectAsync(server.McpEndpoint, httpClient: httpsClient);

        client.ServerInfo.Name.Should().Be("Windows-mcp");
        (await client.ListToolsAsync()).Should().NotBeEmpty();

        // Plaintext on the same port: the TLS handshake fails and nothing is served in the clear.
        using var plain = new HttpClient();
        var plainUrl = new UriBuilder(server.McpEndpoint) { Scheme = "http" }.Uri;
        var act = () => plain.PostAsync(plainUrl, InitializeBody());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Caller_facing_refusals_reach_the_client_verbatim_over_http()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var fileWrite = FileWriteToolName(await client.ListToolsAsync());

        var scratch = Path.Combine(Path.GetTempPath(), $"windows-mcp-http-test-{Guid.NewGuid():N}.txt");
        var result = await client.CallToolAsync(fileWrite, new Dictionary<string, object?>
        {
            ["path"] = scratch,
            ["content"] = "must never be written",
            // no confirm:true -> the tool's ArgumentException, which the shared filter must surface as-is
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Be("'confirm: true' is required for file writes",
                "the ToolErrors filter registered in AddWindowsMcp applies to the HTTP transport too");
        File.Exists(scratch).Should().BeFalse();
    }

    // ---- A-7 ------------------------------------------------------------------------------

    /// <summary>
    /// A-7 risk #1 (A-roadmap section 6): <c>screenshot</c> changes its return type from
    /// <c>string</c> to <see cref="CallToolResult"/>. If <c>WithToolsFromAssembly</c> stopped
    /// discovering it, or the SDK rejected the return type, the tool would vanish from the list —
    /// this catches that without needing a desktop.
    /// </summary>
    [Fact]
    public async Task Screenshot_tool_is_still_discovered_with_a_CallToolResult_return_type()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var screenshot = tools.Single(t => t.Name.Replace("_", "").Equals("screenshot", StringComparison.OrdinalIgnoreCase));

        screenshot.Name.Should().Be(ScreenshotToolName(tools));
        var schema = screenshot.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var parameter in new[] { "region", "format", "output" })
            schema.TryGetProperty(parameter, out _).Should().BeTrue($"'{parameter}' is part of the A-7 signature");
        foreach (var parameter in new[] { "max_width", "max_height", "scale", "quality" })
            schema.TryGetProperty(parameter, out _).Should().BeTrue($"'{parameter}' is part of the A-9 signature");
        schema.TryGetProperty("display", out _).Should().BeTrue("'display' is part of the A-8 signature");
    }

    /// <summary>
    /// A-9 (R1): the schema defaults and the description text are the only spec the model reads
    /// before it calls the tool, so they are a requirement in their own right — an advertised
    /// "default 1920" that the method does not actually apply is a lie the model acts on.
    /// <c>ScreenToolsTests.Screenshot_defaults_pass_the_1920x1080_cap_scale_1_and_quality_90</c>
    /// pins the other half (what the method does with the defaults).
    /// </summary>
    [Fact]
    public async Task Screenshot_schema_advertises_the_downscale_defaults_and_the_coordinate_scale_contract()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var screenshot = tools.Single(t => t.Name == ScreenshotToolName(tools));
        var schema = screenshot.ProtocolTool.InputSchema.GetProperty("properties");

        schema.GetProperty("max_width").GetProperty("default").GetInt32().Should().Be(1920);
        schema.GetProperty("max_height").GetProperty("default").GetInt32().Should().Be(1080);
        schema.GetProperty("scale").GetProperty("default").GetDouble().Should().Be(1.0);
        schema.GetProperty("quality").GetProperty("default").GetInt32().Should().Be(90);

        var description = screenshot.ProtocolTool.Description;
        description.Should().Contain("1920x1080", "the prose default must match the schema default");
        description.Should().Contain("coordinateScale");
        description.Should().Contain("multiply image pixel coordinates",
            "the model is told once, in the tool description, how to undo the downscale");
    }

    /// <summary>
    /// The argument guards run before any capture, so this exercises the new tool over the real
    /// transport on a headless box: no screen is touched, but the call, the filter and the
    /// CallToolResult serialization all are.
    /// </summary>
    [Fact]
    public async Task Screenshot_argument_errors_reach_the_client_over_http()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "not-a-region",   // rejected by ParseRegion before ScreenshotService is called
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("Invalid region", "the ArgumentException is caller-facing and must survive the transport");
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
    }

    // ---- A-9 ------------------------------------------------------------------------------

    /// <summary>
    /// A-9 (R9) end to end over the real transport, with <see cref="IScreenshotService"/> swapped
    /// for a mock through the new <c>BuildHttpApp</c> seam — so the downscale metadata contract is
    /// proven on a headless box, not only in the tool's own unit tests. Everything between the
    /// tool method and the JSON-RPC response (DI, the tool invoker, CallToolResult serialization)
    /// is real.
    /// </summary>
    [Fact]
    public async Task Screenshot_reports_the_original_size_and_coordinate_scale_over_http()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 2, 2, ImageFormat.Png, 4, 4, 2.0));

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(screenshotService.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        // No arguments: the default agent-loop call.
        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>());

        result.IsError.Should().NotBe(true);

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.DecodedData.ToArray().Should().Equal(png, "the mocked bytes must survive the round trip");
        image.MimeType.Should().Be("image/png", "the mime follows the ENCODED format, not the requested one");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(2);
        meta.RootElement.GetProperty("height").GetInt32().Should().Be(2);
        meta.RootElement.GetProperty("originalWidth").GetInt32().Should().Be(4);
        meta.RootElement.GetProperty("originalHeight").GetInt32().Should().Be(4);
        meta.RootElement.GetProperty("coordinateScale").GetDouble().Should().Be(2.0);
        meta.RootElement.GetProperty("note").GetString().Should()
            .Be("multiply image pixel coordinates by 2 before passing them to click/drag/scroll");
    }

    // ---- A-8 ------------------------------------------------------------------------------

    /// <summary>
    /// A-8 (R8) headless, through the real transport: with BOTH collaborators swapped through the
    /// <c>BuildHttpApp</c> seam — a capture mock and a two-monitor <see cref="IWindowService"/> —
    /// <c>screenshot(display:"1")</c> must resolve the second monitor's rect, report it as the
    /// captured region, and list the whole inventory. This is the only place the DI wiring of the
    /// new <c>IWindowService</c> dependency into <c>ScreenTools</c> is exercised at all.
    /// </summary>
    [Fact]
    public async Task Screenshot_display_selects_the_second_monitor_over_http()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 1920, 1080, ImageFormat.Png, 1920, 1080, 1.0));

        var windowService = new Mock<IWindowService>();
        windowService
            .Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true),
                new MonitorInfo(1, "Monitor1", 1920, 0, 1920, 1080, false),
            ]);

        await using var server = await Harness.StartAsync(configureServices: services =>
        {
            services.AddSingleton(screenshotService.Object);
            services.AddSingleton(windowService.Object);
        });
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["display"] = "1",
            ["format"] = "png",
        });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);

        foreach (var field in new[] { "region", "displays", "selectedDisplays" })
            meta.RootElement.TryGetProperty(field, out _).Should()
                .BeTrue($"A-8 metadata must carry '{field}' when display picked the rect");

        var region = meta.RootElement.GetProperty("region");
        region.GetProperty("x").GetInt32().Should().Be(1920, "display 1 starts at x=1920 on this inventory");
        region.GetProperty("y").GetInt32().Should().Be(0);
        region.GetProperty("width").GetInt32().Should().Be(1920);
        region.GetProperty("height").GetInt32().Should().Be(1080);

        meta.RootElement.GetProperty("selectedDisplays").EnumerateArray()
            .Select(e => e.GetInt32()).Should().Equal(new[] { 1 });
        meta.RootElement.GetProperty("displays").GetArrayLength().Should().Be(2, "every monitor is listed");
        meta.RootElement.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");

        screenshotService.Verify(s => s.CaptureAsync(
            new ScreenRegion(1920, 0, 1920, 1080), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "the resolved rect reaches the capture service through the real host");
    }

    // ---- A-11 ------------------------------------------------------------------------------

    /// <summary>
    /// A-11 (R6) headless, through the real transport, with all three collaborators swapped at the
    /// <c>BuildHttpApp</c> seam: the cursor the tool reports must be the one
    /// <see cref="IInputService"/> gave it, and its <c>monitorIndex</c> must be resolved against
    /// the same two-monitor inventory the rect was. This is the only place the new
    /// <c>IInputService</c> dependency of <c>ScreenTools</c> is resolved through real DI.
    /// </summary>
    [Fact]
    public async Task Screenshot_reports_the_cursor_and_its_monitor_over_http()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 1920, 1080, ImageFormat.Png, 1920, 1080, 1.0, "icon"));

        var windowService = new Mock<IWindowService>();
        windowService
            .Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true),
                new MonitorInfo(1, "Monitor1", 1920, 0, 1920, 1080, false),
            ]);

        var inputService = new Mock<IInputService>();
        inputService
            .Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(2000, 10));   // on the second monitor

        await using var server = await Harness.StartAsync(configureServices: services =>
        {
            services.AddSingleton(screenshotService.Object);
            services.AddSingleton(windowService.Object);
            services.AddSingleton(inputService.Object);
        });
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["format"] = "png",
        });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);

        meta.RootElement.TryGetProperty("cursor", out var cursor).Should()
            .BeTrue("A-11 metadata carries 'cursor' on every screenshot");
        cursor.GetProperty("x").GetInt32().Should().Be(2000, "the position comes from IInputService");
        cursor.GetProperty("y").GetInt32().Should().Be(10);
        cursor.GetProperty("monitorIndex").GetInt32().Should()
            .Be(1, "(2000,10) is on the second monitor of this inventory");
        meta.RootElement.GetProperty("cursorDrawn").GetString().Should()
            .Be("icon", "the service reported it drew the real cursor bitmap");

        inputService.Verify(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()),
            Times.Once, "one cursor read per screenshot, through the real host");
        screenshotService.Verify(s => s.CaptureAsync(
            It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.IncludeCursor),
            It.IsAny<CancellationToken>()),
            Times.Once, "include_cursor defaults to true, so the capture is asked to draw it");
    }

    /// <summary>
    /// A-11 (R6): the schema is where the model learns the argument exists and that it is on by
    /// default — an advertised default the method does not apply is a lie the model acts on.
    /// </summary>
    [Fact]
    public async Task Screenshot_schema_advertises_include_cursor_defaulting_to_true()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var screenshot = tools.Single(t => t.Name == ScreenshotToolName(tools));
        var schema = screenshot.ProtocolTool.InputSchema.GetProperty("properties");

        schema.TryGetProperty("include_cursor", out var includeCursor).Should()
            .BeTrue("the parameter must reach the wire schema, not just the method signature");
        includeCursor.GetProperty("default").GetBoolean().Should().BeTrue();
        screenshot.ProtocolTool.Description.Should().Contain("cursor",
            "the metadata list in the description tells the model the cursor field is there");
    }

    // ---- A-6 -------------------------------------------------------------------------------

    /// <summary>
    /// A-6 (R5) through the real transport with all four collaborators swapped at the
    /// <c>BuildHttpApp</c> seam. <c>annotate</c> makes <c>screenshot</c> depend on
    /// <see cref="IUIAutomationService"/> as well, and this is the only place that new DI edge is
    /// resolved for real; the three content blocks (metadata, element list, picture) also have to
    /// survive the JSON-RPC round trip in that order.
    /// </summary>
    [Fact]
    public async Task Screenshot_annotate_returns_the_element_list_beside_the_image_over_http()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 1920, 1080, ImageFormat.Png, 1920, 1080, 1.0, null, 1));

        var windowService = new Mock<IWindowService>();
        windowService
            .Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);

        var inputService = new Mock<IInputService>();
        inputService
            .Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(612, 388));

        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedSnapshot);   // el_12 at (600,380,24,16) — inside the primary rect

        await using var server = await Harness.StartAsync(configureServices: services =>
        {
            services.AddSingleton(screenshotService.Object);
            services.AddSingleton(windowService.Object);
            services.AddSingleton(inputService.Object);
            services.AddSingleton(uia.Object);
        });
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["format"] = "png",
            ["annotate"] = true,
        });

        result.IsError.Should().NotBe(true);
        result.Content.Should().HaveCount(3, "metadata, the element list, then the annotated picture");
        result.Content[2].Should().BeOfType<ImageContentBlock>();

        using var meta = JsonDocument.Parse(result.Content[0].Should().BeOfType<TextContentBlock>().Subject.Text);
        meta.RootElement.GetProperty("annotated").GetBoolean().Should().BeTrue();
        meta.RootElement.GetProperty("annotations").GetInt32().Should()
            .Be(1, "the count the capture service reported reaches the client");

        result.Content[1].Should().BeOfType<TextContentBlock>().Subject.Text.Should()
            .Contain("el_12", "label N in the picture is row N of this block, from the same call");

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Desktop), It.IsAny<CancellationToken>()),
            Times.Once, "one desktop walk per annotated screenshot, through the real host");
        screenshotService.Verify(s => s.CaptureAsync(
            It.IsAny<ScreenRegion?>(),
            It.Is<CaptureOptions>(o => o.Annotations != null && o.Annotations.Count == 1
                                       && o.Annotations[0].Label == "el_12"),
            It.IsAny<CancellationToken>()),
            Times.Once, "the boxes are built from the snapshot and handed to the capture");
    }

    // ---- A-2 -------------------------------------------------------------------------------

    /// <summary>
    /// A-2 (R6): <c>snapshot</c> is the 65th tool and the only new one in section A. Discovery is
    /// where a source-generated tool goes missing, and the schema is the whole spec the model
    /// reads before its first call - a parameter that is not advertised does not exist.
    /// </summary>
    [Fact]
    public async Task Snapshot_tool_is_discovered_with_the_A2_parameter_set()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var snapshot = tools.Single(t => t.Name == SnapshotToolName(tools));

        var schema = snapshot.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var parameter in new[] { "scope", "window", "include_tree", "max_elements", "format", "use_dom" })
            schema.TryGetProperty(parameter, out _).Should().BeTrue($"'{parameter}' is part of the A-2 signature");

        schema.GetProperty("scope").GetProperty("default").GetString().Should().Be("desktop");
        schema.GetProperty("format").GetProperty("default").GetString().Should().Be("text",
            "roadmap C6: text is the default, json is the opt-in");
        schema.GetProperty("max_elements").GetProperty("default").GetInt32().Should().Be(0,
            "0 means the server budget from --max-tree-elements");
        schema.GetProperty("include_tree").GetProperty("default").GetBoolean().Should().BeFalse();
        schema.GetProperty("use_dom").GetProperty("default").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// A-2 (R6) with <see cref="IUIAutomationService"/> swapped at the <c>BuildHttpApp</c> seam:
    /// the JSON form must survive DI, the tool invoker and the JSON-RPC round trip with the
    /// element ids intact - they are what <c>click</c> and <c>interact_element</c> are given next.
    /// </summary>
    [Fact]
    public async Task Snapshot_json_over_http_carries_the_interactive_elements()
    {
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedSnapshot);

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(uia.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var snapshot = SnapshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(snapshot, new Dictionary<string, object?>
        {
            ["format"] = "json",
        });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        using var doc = JsonDocument.Parse(text);

        var element = doc.RootElement.GetProperty("Interactive")[0];
        element.GetProperty("ElementId").GetString().Should().Be("el_12");
        element.GetProperty("Action").GetString().Should().Be("click");
        element.GetProperty("CenterX").GetInt32().Should().Be(612);
        doc.RootElement.GetProperty("Windows").GetArrayLength().Should().Be(1);

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Desktop), It.IsAny<CancellationToken>()),
            Times.Once, "one service call per tool call, with the default scope");
    }

    [Fact]
    public async Task Snapshot_text_over_http_is_the_rendered_layout()
    {
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedSnapshot);

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(uia.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var snapshot = SnapshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(snapshot, new Dictionary<string, object?>());

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        text.Should().StartWith("Cursor:", "text is the default format and the header comes first");
        text.Should().Contain("Interactive (").And.Contain("el_12");
    }

    /// <summary>
    /// A-5 phase 1 (R7): <c>use_dom</c> reaches the service as a request flag over the wire, and
    /// the Pages block comes back through the JSON-RPC round trip. Before A-5 this call was a tool
    /// error; the service is mocked here because the transport, not the browser, is what is under
    /// test — <c>HttpTransportDomSnapshotTests</c> is the non-mocked sibling.
    /// </summary>
    [Fact]
    public async Task Snapshot_use_dom_over_http_forwards_the_flag_and_returns_the_pages()
    {
        var page = new SnapshotPage("A5 Probe Page", "el_7", "A5 Probe Page", "http://127.0.0.1:9999/a5",
            new ScrollInfo(12, 0, true, false), ["Probe heading"], null);
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedSnapshot with { Pages = [page] });

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(uia.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var snapshot = SnapshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(snapshot, new Dictionary<string, object?>
        {
            ["use_dom"] = true,
            ["format"] = "json",
        });

        result.IsError.Should().NotBe(true, "use_dom is implemented for Chromium as of A-5 phase 1");
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        using var doc = JsonDocument.Parse(text);
        var reported = doc.RootElement.GetProperty("Pages")[0];
        reported.GetProperty("DocumentId").GetString().Should().Be("el_7");
        reported.GetProperty("Url").GetString().Should().Be("http://127.0.0.1:9999/a5");
        reported.GetProperty("Text")[0].GetString().Should().Be("Probe heading");

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.UseDom), It.IsAny<CancellationToken>()),
            Times.Once, "the flag the caller sent is the flag the service is given");
    }

    internal static string WindowToolName(IEnumerable<McpClientTool> tools) =>
        tools.Single(t => t.Name.Replace("_", "").Equals("window", StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>
    /// A-12 phase 1 (R6): <c>window(action:"desktops")</c> through the real host with the real
    /// services — the one test that fails if <c>IVirtualDesktopService</c> is never registered in
    /// <c>AddWindowsMcp</c> (WindowTools takes it as a constructor argument, so the tool call
    /// dies with a DI error). Shape only: a box may legitimately list no desktops.
    /// </summary>
    [Fact]
    public async Task Window_desktops_over_http_returns_the_desktop_envelope()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var window = WindowToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(window, new Dictionary<string, object?>
        {
            ["action"] = "desktops",
        });

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        result.IsError.Should().NotBe(true,
            "an unusual virtual-desktop registry layout is data, not an error - the server said: {0}", text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("all").ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.TryGetProperty("current", out var current).Should().BeTrue();
        current.ValueKind.Should().BeOneOf(JsonValueKind.Null, JsonValueKind.Object);
        foreach (var desktop in doc.RootElement.GetProperty("all").EnumerateArray())
        {
            desktop.GetProperty("Id").GetString().Should().NotBeNullOrWhiteSpace();
            desktop.GetProperty("Name").GetString().Should().NotBeNullOrWhiteSpace();
            desktop.TryGetProperty("Index", out _).Should().BeTrue();
            desktop.TryGetProperty("IsCurrent", out _).Should().BeTrue();
        }
    }

    // ---- B-8 / B-9: the launch and window schemas ---------------------------------------------
    // The schema is the whole spec the model reads before its first call: a parameter that is not
    // advertised does not exist, and a default that disagrees with the description is a trap.

    /// <summary>
    /// B-8 (R7): <c>launch</c> gained two parameters and lost its "just a name" shape. Discovery
    /// through the real host is where a source-generated signature goes missing.
    /// </summary>
    [Fact]
    public async Task Launch_tool_is_discovered_with_the_B8_parameter_set()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var launch = tools.Single(t => t.Name == ToolName(tools, "launch"));
        var schema = launch.ProtocolTool.InputSchema.GetProperty("properties");

        schema.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["app_name", "wait_for_window", "timeout_ms"],
            "no more and no fewer: the catalog and the poll interval are the server's business");
        schema.GetProperty("wait_for_window").GetProperty("default").GetBoolean().Should().BeTrue();
        schema.GetProperty("timeout_ms").GetProperty("default").GetInt32().Should().Be(10_000);
        launch.ProtocolTool.Description.Should().ContainEquivalentOf("Start Menu");
    }

    /// <summary>
    /// B-9 (R7): <c>window</c> gained five parameters, and the A-1/B-10 five have to stay exactly
    /// where they were - callers pass <c>action</c> and <c>title</c> positionally.
    /// </summary>
    [Fact]
    public async Task Window_tool_is_discovered_with_the_B9_parameter_set()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var window = tools.Single(t => t.Name == WindowToolName(tools));
        var schema = window.ProtocolTool.InputSchema.GetProperty("properties");

        schema.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["action", "title", "include_minimized", "include_hidden", "hwnd",
             "x", "y", "width", "height", "restore_first"]);
        schema.GetProperty("restore_first").GetProperty("default").GetBoolean().Should().BeFalse();
        window.ProtocolTool.Description.Should().Contain("set_bounds");
    }

    /// <summary>
    /// B-9 end to end with <see cref="IWindowService"/> swapped at the <c>BuildHttpApp</c> seam:
    /// the nested Before/After rectangles have to survive DI, the tool invoker and the JSON-RPC
    /// round trip. Nothing on this desktop moves - the service is a mock.
    /// </summary>
    [Fact]
    public async Task Window_set_bounds_over_http_returns_both_rectangles()
    {
        var windowService = new Mock<IWindowService>();
        windowService.Setup(w => w.SetBoundsAsync("notepad", null, 100, 100, 800, 600, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowBoundsResult(
                new WindowInfo("Untitled - Notepad", 0x1234, 4242, "notepad", WindowState.Normal,
                    new Bounds(100, 100, 800, 600), 0, true, false, 0),
                new Bounds(10, 20, 300, 200), new Bounds(100, 100, 800, 600), "substring", 100, false));

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(windowService.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var window = WindowToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(window, new Dictionary<string, object?>
        {
            ["action"] = "set_bounds",
            ["title"] = "notepad",
            ["x"] = 100,
            ["y"] = 100,
            ["width"] = 800,
            ["height"] = 600,
        });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("Before").GetProperty("Width").GetInt32().Should().Be(300);
        doc.RootElement.GetProperty("After").GetProperty("Width").GetInt32().Should().Be(800);
        doc.RootElement.GetProperty("Window").GetProperty("Hwnd").GetInt64().Should().Be(0x1234);
        windowService.Verify(w => w.SetBoundsAsync("notepad", null, 100, 100, 800, 600, false, It.IsAny<CancellationToken>()),
            Times.Once, "the numbers the caller sent are the numbers the service is given");
    }

    // ---- B-5: wait ---------------------------------------------------------------------------

    private static string ToolName(IEnumerable<McpClientTool> tools, string name) =>
        tools.Single(t => t.Name.Replace("_", "").Equals(name, StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>
    /// B-5: the annotations are only worth setting if a client can see them. The attribute is
    /// pinned by reflection in <c>InputToolsTests</c>; this is the same fact over the wire, where
    /// the SDK has to have turned <c>ReadOnly</c>/<c>Idempotent</c> into the protocol's
    /// <c>annotations.readOnlyHint</c>/<c>idempotentHint</c>.
    /// </summary>
    [Fact]
    public async Task Wait_is_advertised_with_its_read_only_and_idempotent_hints()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var wait = tools.Single(t => t.Name.Equals(ToolName(tools, "wait"), StringComparison.Ordinal));

        wait.ProtocolTool.Annotations.Should().NotBeNull("the tool carries annotations to the client");
        wait.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
        wait.ProtocolTool.Annotations.IdempotentHint.Should().BeTrue();
        wait.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("seconds", out _)
            .Should().BeTrue("'seconds' is the whole signature");
    }

    [Fact]
    public async Task Wait_waits_over_the_wire_and_refuses_an_out_of_range_request()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var wait = ToolName(await client.ListToolsAsync(), "wait");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ok = await client.CallToolAsync(wait, new Dictionary<string, object?> { ["seconds"] = 0.2 });
        stopwatch.Stop();

        ok.IsError.Should().NotBe(true);
        ok.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("\"waited\"");
        stopwatch.Elapsed.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(200 - 16);

        var refused = await client.CallToolAsync(wait, new Dictionary<string, object?> { ["seconds"] = 3600 });

        refused.IsError.Should().BeTrue();
        refused.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("60").And.Contain("wait_for");
    }

    // ---- B-6 / B-7: the phase-4 surface over the wire ------------------------------------------

    /// <summary>
    /// B-6 (R71): <c>wait_for</c> gained two parameters and the seven it had must keep their
    /// names and defaults — a model that learned the old schema keeps calling it that way.
    /// </summary>
    [Fact]
    public async Task Wait_for_tool_is_discovered_with_the_B6_parameter_set()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var waitFor = tools.Single(t => t.Name == ToolName(tools, "waitfor"));
        var schema = waitFor.ProtocolTool.InputSchema.GetProperty("properties");

        schema.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["text", "timeout_ms", "interval_ms", "kind", "scope", "window", "include_offscreen",
             "condition", "use_dom"]);
        schema.GetProperty("condition").GetProperty("default").GetString().Should().Be("element_exists",
            "the default is exactly what the tool did before B-6");
        schema.GetProperty("use_dom").GetProperty("default").GetBoolean().Should().BeFalse();
        schema.GetProperty("timeout_ms").GetProperty("default").GetInt32().Should().Be(10_000);
        schema.GetProperty("interval_ms").GetProperty("default").GetInt32().Should().Be(500);
        waitFor.ProtocolTool.Description.Should().Contain("active_window").And.Contain("text_exists");
    }

    /// <summary>
    /// B-6 (R72) end to end with <see cref="IUIAutomationService"/> swapped at the
    /// <c>BuildHttpApp</c> seam: the structured result has to survive DI, the tool invoker and the
    /// JSON-RPC round trip, and a timeout must not come back as an error or as "null". Nothing on
    /// this desktop is waited on — the service is a mock.
    /// </summary>
    [Fact]
    public async Task Wait_for_over_http_returns_a_structured_result_on_timeout()
    {
        var uia = new Mock<IUIAutomationService>();
        WaitRequest? seen = null;
        uia.Setup(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()))
            .Callback((WaitRequest r, CancellationToken _) => seen = r)
            .ReturnsAsync(new WaitForResult(false, "active_window", 2000, 4, "active window is 'Calculator', wanted 'Notepad'"));

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(uia.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var waitFor = ToolName(await client.ListToolsAsync(), "waitfor");

        var result = await client.CallToolAsync(waitFor, new Dictionary<string, object?>
        {
            ["text"] = "Notepad",
            ["condition"] = "window",
            ["timeout_ms"] = 2000,
        });

        result.IsError.Should().NotBe(true, "a timeout is an outcome, not a tool error (roadmap C4)");
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        text.Should().NotBe("null");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("Satisfied").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("Attempts").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("Detail").GetString().Should().Contain("wanted 'Notepad'");
        seen.Should().NotBeNull();
        seen!.Condition.Should().Be(WaitCondition.ActiveWindow, "the alias 'window' survived the wire");
        seen.TimeoutMs.Should().Be(2000);
    }

    /// <summary>
    /// B-7 (R111): the two new tools exist on the wire with upstream's names, their parameter sets
    /// and no read-only/idempotent hints — they inject input.
    /// </summary>
    [Fact]
    public async Task The_batch_tools_are_discovered_with_their_parameter_sets_and_no_safe_hints()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var select = tools.Single(t => t.Name == ToolName(tools, "multiselect"));
        var edit = tools.Single(t => t.Name == ToolName(tools, "multiedit"));

        select.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["targets_json", "ctrl"]);
        select.ProtocolTool.InputSchema.GetProperty("properties")
            .GetProperty("ctrl").GetProperty("default").GetBoolean().Should().BeTrue();
        edit.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["entries_json"]);

        foreach (var tool in new[] { select, edit })
        {
            tool.ProtocolTool.Annotations?.ReadOnlyHint.Should().NotBe(true, $"{tool.Name} clicks and types");
            tool.ProtocolTool.Annotations?.IdempotentHint.Should().NotBe(true);
            tool.ProtocolTool.Description.Should().NotContainEquivalentOf("not implemented");
        }
    }

    /// <summary>
    /// B-7 (R112): a batch refusal has to reach the client as a tool error naming the parameter,
    /// not as a JSON parser exception. The input service is a mock, so nothing is clicked.
    /// </summary>
    [Fact]
    public async Task Multi_select_over_http_refuses_a_malformed_batch_by_name()
    {
        var input = new Mock<IInputService>();
        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(input.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var select = ToolName(await client.ListToolsAsync(), "multiselect");

        var refused = await client.CallToolAsync(select, new Dictionary<string, object?>
        {
            ["targets_json"] = "[{\"x\":1}]",
        });

        refused.IsError.Should().BeTrue();
        refused.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("targets_json");
        input.Verify(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- B-12: multi_monitor detail over the wire ---------------------------------------------

    /// <summary>
    /// B-12 end to end with the real <c>WindowService</c>: the four new fields have to survive
    /// serialisation, and their values have to be consistent with the bounds beside them.
    /// Values, not just presence — <c>WorkArea: null, Scale: 1</c> on every monitor would satisfy
    /// a presence-only check while telling the model nothing.
    /// </summary>
    [Fact]
    public async Task Multi_monitor_over_http_carries_the_work_area_orientation_dpi_and_scale()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var multiMonitor = ToolName(await client.ListToolsAsync(), "multimonitor");

        var result = await client.CallToolAsync(multiMonitor, new Dictionary<string, object?>());

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        result.IsError.Should().NotBe(true, "the server said: {0}", text);
        using var doc = JsonDocument.Parse(text);
        var monitors = doc.RootElement.EnumerateArray().ToArray();
        monitors.Should().NotBeEmpty("this session has at least one display");
        foreach (var m in monitors)
        {
            var work = m.GetProperty("WorkArea");
            work.ValueKind.Should().Be(JsonValueKind.Object, "every monitor reports its work area");
            work.GetProperty("Height").GetInt32().Should().BePositive()
                .And.BeLessThanOrEqualTo(m.GetProperty("Height").GetInt32());
            m.GetProperty("Orientation").GetInt32().Should().BeOneOf(0, 90, 180, 270);
            int dpi = m.GetProperty("EffectiveDpi").GetInt32();
            dpi.Should().BeGreaterThanOrEqualTo(96);
            m.GetProperty("Scale").GetDouble().Should().BeApproximately(dpi / 96.0, 1e-9);
        }
    }

    /// <summary>One window, one element, one scrollable - enough that a dropped block shows.</summary>
    private static SnapshotResult FixedSnapshot
    {
        get
        {
            var window = new WindowInfo("Untitled - Notepad", 1, 4242, "notepad", WindowState.Normal,
                new Bounds(100, 100, 800, 600), 0, true, false, 0);
            var element = new SnapshotElement("el_12", "Untitled - Notepad", "Button", "Save", 612, 388,
                new Bounds(600, 380, 24, 16), "click", false, false, null, null, null, "Ctrl+S", null, null, null);
            var scrollable = new SnapshotScrollable("el_20", "Untitled - Notepad", "Document", "Text Editor",
                500, 400, new Bounds(100, 140, 800, 520), new ScrollInfo(37, 0, true, false));
            return new SnapshotResult([window], window, new CursorPosition(612, 388), 0,
                [element], [scrollable], null, false, 500, 57, 12);
        }
    }

    // ---- A-14 ------------------------------------------------------------------------------

    /// <summary>
    /// A-14 (R5): the flash reaches the tool through DI. <c>ScreenToolsTests</c> constructs the
    /// tool by hand and would stay green if <c>IFlashOverlay</c> were never registered — the whole
    /// screenshot surface would then fail to resolve at run time. Every collaborator the tool needs
    /// is mocked here, so this is the DI edge and nothing else.
    /// </summary>
    [Fact]
    public async Task Screenshot_shows_the_flash_overlay_resolved_from_the_container()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 1920, 1080, ImageFormat.Png, 1920, 1080, 1.0));
        var windowService = new Mock<IWindowService>();
        windowService.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var inputService = new Mock<IInputService>();
        inputService.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(100, 100));
        var uia = new Mock<IUIAutomationService>();
        var flash = new Mock<IFlashOverlay>();

        await using var server = await Harness.StartAsync(configureServices: services =>
        {
            services.AddSingleton(screenshotService.Object);
            services.AddSingleton(windowService.Object);
            services.AddSingleton(inputService.Object);
            services.AddSingleton(uia.Object);
            services.AddSingleton(flash.Object);
        });
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?> { ["format"] = "png" });

        result.IsError.Should().NotBe(true);
        flash.Verify(f => f.Hide(), Times.Once, "the previous glow comes down before the shutter");
        flash.Verify(f => f.Show(new ScreenRegion(0, 0, 1920, 1080), TimeSpan.FromSeconds(3.5)), Times.Once,
            "the overlay the container handed the tool is the one that flashes");
    }

    /// <summary>
    /// A-14 adds no tool arguments: the flash and the profiling are process options
    /// (<c>--flash</c>, <c>--profile-snapshot</c>, roadmap C7), not per-call parameters. The schema
    /// is the whole spec the model reads, so pinning the exact property set is what stops a knob
    /// from leaking into it.
    /// </summary>
    [Fact]
    public async Task Screenshot_and_snapshot_schemas_gained_no_parameters_in_A14()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var screenshot = tools.Single(t => t.Name == ScreenshotToolName(tools));
        screenshot.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).Should().BeEquivalentTo(
            [
                "region", "display", "format", "output", "max_width", "max_height",
                "scale", "quality", "include_cursor", "annotate", "grid_columns", "grid_rows",
                // A-10's 'backend' is the one argument added since; the flash and the profiling
                // switches are still process options and still absent.
                "backend",
            ], "no flash or profiling argument belongs on the tool");

        var snapshot = tools.Single(t => t.Name == SnapshotToolName(tools));
        snapshot.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).Should().BeEquivalentTo(
            ["scope", "window", "include_tree", "max_elements", "format", "use_dom"]);
    }

    /// <summary>
    /// The seam itself: a service registered through <c>configureServices</c> must be the one the
    /// tools resolve. Without this, the test above could pass for the wrong reason on a machine
    /// whose real screen happens to satisfy an assertion.
    /// </summary>
    [Fact]
    public async Task Configure_services_replaces_the_registration_AddWindowsMcp_made()
    {
        var screenshotService = new Mock<IScreenshotService>();

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(screenshotService.Object));

        server.App.Services.GetRequiredService<IScreenshotService>()
            .Should().BeSameAs(screenshotService.Object, "configureServices runs AFTER AddWindowsMcp");
    }

    // ---- A-10 ------------------------------------------------------------------------------

    /// <summary>
    /// A-10 (R5): the schema is where the model learns the argument exists and what it defaults
    /// to. An advertised default the method does not apply is a lie the model acts on
    /// (<c>ScreenToolsTests.Screenshot_defaults_to_the_process_backend</c> pins the other half).
    /// </summary>
    [Fact]
    public async Task Screenshot_schema_advertises_the_backend_argument_defaulting_to_auto()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var screenshot = tools.Single(t => t.Name == ScreenshotToolName(tools));
        var schema = screenshot.ProtocolTool.InputSchema.GetProperty("properties");

        schema.TryGetProperty("backend", out var backend).Should()
            .BeTrue("the parameter must reach the wire schema, not just the method signature");
        backend.GetProperty("default").GetString().Should().Be("auto");
        screenshot.ProtocolTool.Description.Should().Contain("backend",
            "the metadata list in the description tells the model the field is there");
    }

    /// <summary>
    /// A-10 (R5) through the real transport with <see cref="IScreenshotService"/> swapped at the
    /// <c>BuildHttpApp</c> seam: the backend the service reports is what the metadata carries, over
    /// the wire, for a client that has no other way to know which backend served it.
    /// </summary>
    [Fact]
    public async Task Screenshot_reports_the_backend_that_produced_the_frame_over_http()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 1920, 1080, ImageFormat.Png, 1920, 1080, 1.0, null, 0, null, "wgc"));

        await using var server = await Harness.StartAsync(configureServices: services =>
            services.AddSingleton(screenshotService.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["format"] = "png",
            ["backend"] = "auto",
        });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);

        meta.RootElement.TryGetProperty("backend", out var backend).Should()
            .BeTrue("A-10 metadata carries 'backend' on every screenshot");
        backend.GetString().Should().Be("wgc", "the picture came from the compositor, whatever was asked for");

        screenshotService.Verify(s => s.CaptureAsync(
            It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.Backend == "auto"),
            It.IsAny<CancellationToken>()),
            Times.Once, "the requested backend reaches the capture service through the real host");
    }

    // ---- B phase 2: the four input verbs' advertised schemas ----------------------------------

    /// <summary>
    /// The properties and the required list a client sees for one tool. B phase 2 changes all four
    /// input verbs' schemas (optional coordinates, element_id, direction first), which is a
    /// contract change for every remote caller - so it is pinned over the wire, not by reflection.
    /// </summary>
    private static (JsonElement Properties, string[] Required) SchemaOf(IEnumerable<McpClientTool> tools, string name)
    {
        var resolved = ToolName(tools, name);
        var tool = tools.Single(t => t.Name.Equals(resolved, StringComparison.Ordinal));
        var schema = tool.ProtocolTool.InputSchema;
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();
        return (schema.GetProperty("properties"), required);
    }

    private static void ShouldAdvertise(JsonElement properties, params string[] names)
    {
        foreach (var name in names)
            properties.TryGetProperty(name, out _).Should().BeTrue($"the tool advertises '{name}'");
    }

    [Fact]
    public async Task Click_is_advertised_with_optional_coordinates_and_an_element_id()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "click");

        ShouldAdvertise(properties, "x", "y", "element_id", "button", "clicks");
        required.Should().BeEmpty(
            "B-4: a click by element_id gives no coordinates, so x and y stop being required");
    }

    [Fact]
    public async Task Type_is_advertised_with_the_target_and_the_editing_options()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "type");

        ShouldAdvertise(properties, "text", "x", "y", "element_id", "clear", "caret", "press_enter", "pace_ms");
        // B-1 adds options around the text; the text itself stays the only requirement.
        required.Should().Equal("text");
    }

    [Fact]
    public async Task Scroll_is_advertised_with_the_direction_required_and_the_coordinates_optional()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "scroll");

        ShouldAdvertise(properties, "direction", "amount", "x", "y", "element_id", "shift_wheel");
        // B-3: x and y move from required to optional - the schema change CHANGELOG has to carry.
        required.Should().Equal("direction");
    }

    [Fact]
    public async Task Drag_is_advertised_with_both_ends_optional_and_the_motion_controls()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "drag");

        ShouldAdvertise(properties,
            "from_x", "from_y", "to_x", "to_y", "element_id", "from_element_id", "button", "duration_ms", "steps");
        required.Should().BeEmpty(
            "B-2: the origin defaults to the cursor and the destination may be an element id");
    }

    // ---- C-7: the annotations as a client sees them -------------------------------------------

    /// <summary>
    /// C-7 (R4): the reflection test in <c>ToolInventoryTests</c> proves the attributes are
    /// written; this proves the SDK turns all five into the protocol's <c>annotations</c> block.
    /// A client auto-approves on <c>readOnlyHint</c> and confirms on <c>destructiveHint</c>, so a
    /// missing hint is a tool the client cannot classify at all.
    /// </summary>
    [Fact]
    public async Task Every_tool_is_advertised_with_a_title_and_all_four_hints()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();

        var unannotated = tools
            .Where(t => t.ProtocolTool.Annotations is null
                        || string.IsNullOrWhiteSpace(t.ProtocolTool.Annotations.Title)
                        || t.ProtocolTool.Annotations.ReadOnlyHint is null
                        || t.ProtocolTool.Annotations.DestructiveHint is null
                        || t.ProtocolTool.Annotations.IdempotentHint is null
                        || t.ProtocolTool.Annotations.OpenWorldHint is null)
            .Select(t => t.Name)
            .ToArray();

        unannotated.Should().BeEmpty("every tool carries a title and all four hints over the wire");
    }

    /// <summary>C-7 (R4): the three rows of the table a reviewer would check first.</summary>
    [Fact]
    public async Task Screenshot_is_read_only_file_manage_destructive_and_scrape_open_world()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        ToolAnnotations Hints(string name)
        {
            var tool = tools.Single(t => t.Name == ToolName(tools, name));
            tool.ProtocolTool.Annotations.Should().NotBeNull("{0} carries the C-7 hints", name);
            return tool.ProtocolTool.Annotations!;
        }

        Hints("screenshot").ReadOnlyHint.Should().BeTrue(
            "the flash overlay is transient; nothing durable changes");
        Hints("screenshot").DestructiveHint.Should().BeFalse();
        Hints("filemanage").DestructiveHint.Should().BeTrue("file_manage deletes and replaces files");
        Hints("filemanage").ReadOnlyHint.Should().BeFalse();
        Hints("scrape").OpenWorldHint.Should().BeTrue(
            "scrape fetches a URL from outside this machine");
    }

    // ---- C-2: registry_delete over the wire ---------------------------------------------------

    /// <summary>C-2 (R5): the new tool's advertised signature.</summary>
    [Fact]
    public async Task Registry_delete_is_advertised_with_the_value_name_recursive_and_confirm()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "registrydelete");

        ShouldAdvertise(properties, "hive", "path", "value_name", "recursive", "confirm");
        properties.GetProperty("recursive").GetProperty("default").GetBoolean().Should().BeFalse(
            "a key delete must not take the tree unless the caller asks");
        properties.GetProperty("confirm").GetProperty("default").GetBoolean().Should().BeFalse();
        required.Should().BeEquivalentTo(new[] { "hive", "path" },
            "everything else has a default; the hive and the path do not");
    }

    /// <summary>
    /// C-2 (R5): the refusal a client actually sees. The real RegistryService is wired in here, so
    /// this also proves the confirm gate runs before anything touches the registry.
    /// </summary>
    [Fact]
    public async Task Registry_delete_over_http_refuses_without_confirm()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var registryDelete = ToolName(await client.ListToolsAsync(), "registrydelete");

        var refused = await client.CallToolAsync(registryDelete, new Dictionary<string, object?>
        {
            ["hive"] = "HKCU",
            ["path"] = @"Software\WindowsMcpTests\does-not-exist",
        });

        refused.IsError.Should().BeTrue();
        refused.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("confirm");
    }

    /// <summary>C-2 (R5): a guarded root is refused by name even with confirm.</summary>
    [Fact]
    public async Task Registry_delete_over_http_refuses_a_guarded_root_by_name()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var registryDelete = ToolName(await client.ListToolsAsync(), "registrydelete");

        var refused = await client.CallToolAsync(registryDelete, new Dictionary<string, object?>
        {
            ["hive"] = "HKCU",
            ["path"] = "Software",
            ["recursive"] = true,
            ["confirm"] = true,
        });

        refused.IsError.Should().BeTrue("HKCU\\Software is the profile's whole software hive");
        refused.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("Software");
    }

    // ---- C-4: the notification's app_id -------------------------------------------------------

    /// <summary>C-4 (R5): the parameter and its default as a remote client sees them.</summary>
    [Fact]
    public async Task Notification_is_advertised_with_an_app_id_defaulting_to_the_servers_aumid()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "notification");

        ShouldAdvertise(properties, "title", "message", "app_id");
        properties.GetProperty("app_id").GetProperty("default").GetString().Should().Be("Windows-MCP");
        required.Should().BeEquivalentTo(new[] { "title", "message" });
    }

    // ---- C-1 / C-3 phase 2: the new parameters on the wire ------------------------------------

    /// <summary>
    /// C-1 R4: a parameter the model cannot see is a parameter that does not exist. The schema is
    /// where <c>offset_lines</c>, <c>append</c> and <c>overwrite</c> become real, and the defaults
    /// in it are the safe ones the design settled on.
    /// </summary>
    [Fact]
    public async Task The_file_tools_expose_their_new_parameters_with_the_safe_defaults()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var read = tools.Single(t => t.Name == ToolName(tools, "fileread"))
            .ProtocolTool.InputSchema.GetProperty("properties");
        var write = tools.Single(t => t.Name == ToolName(tools, "filewrite"))
            .ProtocolTool.InputSchema.GetProperty("properties");
        var manage = tools.Single(t => t.Name == ToolName(tools, "filemanage"))
            .ProtocolTool.InputSchema.GetProperty("properties");

        read.EnumerateObject().Select(p => p.Name).Should().Contain(new[] { "offset_lines", "limit_lines" });
        read.GetProperty("offset_lines").GetProperty("default").GetInt32().Should().Be(0);
        read.GetProperty("limit_lines").GetProperty("default").GetInt32().Should().Be(0);

        write.EnumerateObject().Select(p => p.Name).Should().Contain(new[] { "append", "create_parents" });
        write.GetProperty("append").GetProperty("default").GetBoolean().Should().BeFalse();
        write.GetProperty("create_parents").GetProperty("default").GetBoolean().Should().BeTrue();

        manage.EnumerateObject().Select(p => p.Name)
            .Should().Contain(new[] { "overwrite", "recursive", "pattern", "include_hidden" });
        manage.GetProperty("overwrite").GetProperty("default").GetBoolean().Should().BeFalse(
            "copy/move refuse an existing destination unless told otherwise");
        manage.GetProperty("recursive").GetProperty("default").GetBoolean().Should().BeFalse(
            "confirm never meant 'and the whole tree under it'");
        manage.GetProperty("include_hidden").GetProperty("default").GetBoolean().Should().BeFalse();
    }

    /// <summary>C-1 (C-7 row flip): an append is not a repeatable write.</summary>
    [Fact]
    public async Task File_write_is_no_longer_advertised_as_idempotent()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        tools.Single(t => t.Name == FileWriteToolName(tools))
            .ProtocolTool.Annotations?.IdempotentHint.Should().NotBe(true,
                "file_write can now append, and appending twice is not the state appending once left");
    }

    /// <summary>
    /// C-1 R1 over the wire: the absolute-path rule is a tool-layer refusal, so it has to reach the
    /// client as a message naming the parameter - not as a FileNotFoundException about a path the
    /// model never wrote.
    /// </summary>
    [Fact]
    public async Task A_relative_path_is_refused_over_the_wire_naming_the_parameter()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "fileread"), new Dictionary<string, object?>
        {
            ["path"] = "notes.txt",
        });

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("path").And.ContainEquivalentOf("absolute");
    }

    /// <summary>C-3 R5: the four new process parameters and their defaults, on the wire.</summary>
    [Fact]
    public async Task The_process_tool_exposes_sort_by_limit_graceful_and_grace_ms()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var schema = tools.Single(t => t.Name == ToolName(tools, "process"))
            .ProtocolTool.InputSchema.GetProperty("properties");

        schema.EnumerateObject().Select(p => p.Name)
            .Should().Contain(new[] { "sort_by", "limit", "graceful", "grace_ms" });
        schema.GetProperty("limit").GetProperty("default").GetInt32().Should().Be(0,
            "0 = all, so no existing caller silently loses rows");
        schema.GetProperty("graceful").GetProperty("default").GetBoolean().Should().BeFalse(
            "today's hard kill stays the default");
        schema.GetProperty("grace_ms").GetProperty("default").GetInt32().Should().Be(3000);
    }

    // ---- C-1 round 4: the cap on the wire, and the refusals that stopped being readable --------

    /// <summary>C-1 R4-5: the cap has to be visible, and its default has to be the safe one.</summary>
    [Fact]
    public async Task File_manage_advertises_the_listing_cap_with_a_thousand_row_default()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, _) = SchemaOf(await client.ListToolsAsync(), "filemanage");

        ShouldAdvertise(properties, "max_entries");
        properties.GetProperty("max_entries").GetProperty("default").GetInt32().Should().Be(1000,
            "a recursive listing of C:\\Windows was 160 000 entries in one response");
    }

    /// <summary>
    /// C-1 R4-7: <c>file_write(create_parents:false)</c> refuses a missing directory by naming the
    /// flag that would create it — a DirectoryNotFoundException, which the CallTool filter masks
    /// today as "An error occurred invoking 'file_write'". The message is the entire value of the
    /// refusal, so this is the test that says it must arrive.
    /// </summary>
    [Fact]
    public async Task File_write_refusing_a_missing_parent_reaches_the_client_naming_the_flag()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();
        var directory = Path.Combine(Path.GetTempPath(), "wmcp-http-parents-" + Guid.NewGuid().ToString("N"));

        var result = await client.CallToolAsync(ToolName(tools, "filewrite"), new Dictionary<string, object?>
        {
            ["path"] = Path.Combine(directory, "note.txt"),
            ["content"] = "must never be written",
            ["confirm"] = true,
            ["create_parents"] = false,
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("create_parents", "a caller that cannot read the refusal cannot act on it");
        Directory.Exists(directory).Should().BeFalse("create_parents:false means create nothing");
    }

    /// <summary>C-1 R4-7: registry_get's "not found" is an answer, not a fault.</summary>
    [Fact]
    public async Task Registry_get_on_a_missing_key_reaches_the_client_verbatim()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "registryget"), new Dictionary<string, object?>
        {
            ["hive"] = "HKCU",
            ["path"] = @"Software\WindowsMcpTests\does-not-exist",
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("Registry path not found",
                "the tool's own description promises 'a missing key is an error naming the path'");
    }

    /// <summary>
    /// C-1 R4-7: the window matcher answers a miss with the list of what IS open, which is the
    /// caller's next move. Read-only — it enumerates the desktop's windows and refuses before
    /// touching any of them, so it is Integration rather than UIAutomation.
    /// </summary>
    [Fact]
    public async Task Switch_to_window_with_no_match_reaches_the_client_listing_the_open_windows()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "switchtowindow"), new Dictionary<string, object?>
        {
            ["title"] = "no-such-window-" + Guid.NewGuid().ToString("N"),
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("Open windows", "the miss is only actionable with the inventory beside it");
    }

    /// <summary>
    /// S-11 (D1): the harness runs with the control indicator off, so a driving tool called by any
    /// test in this class neither draws on the desktop nor takes the user's real takeover
    /// combination. Asserted on the container's REAL indicator after a real driving call, not on
    /// the options record alone: an options record that said "off" while the indicator was built
    /// from <c>ControlIndicatorOptions.Default</c> would still draw.
    /// </summary>
    [Fact]
    public async Task The_harness_runs_with_the_control_indicator_off_so_driving_tools_draw_nothing()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        await client.CallToolAsync(ToolName(tools, "switchtowindow"), new Dictionary<string, object?>
        {
            ["title"] = "no-such-window-" + Guid.NewGuid().ToString("N"),
        });

        server.App.Services.GetRequiredService<ControlIndicatorOptions>().Enabled.Should().BeFalse();
        var indicator = server.App.Services.GetRequiredService<IControlIndicator>()
            .Should().BeOfType<WindowsMcp.Services.ControlIndicator>("the harness keeps the real registration, switched off").Subject;
        indicator.IsVisible.Should().BeFalse("switch_to_window is a driving tool, and the harness's indicator is off");
        indicator.RingHandle.Should().Be(0, "off means no window is ever created");
        indicator.BannerHandle.Should().Be(0);
        indicator.IsHotkeyRegistered.Should().BeFalse("and the user's real combination is never registered by a test");
    }

    /// <summary>
    /// D-10 (R1): the checklist's "Tests." line — a toggle on a control with no TogglePattern
    /// surfaces through the call-tool filter naming the pattern, the control type and the name,
    /// not as "An error occurred invoking 'interact_element'.". The service is mocked to throw the
    /// exact refusal the real one builds (<c>UiRefusals.PatternNotSupported</c>), because a FlaUI
    /// element cannot be faked; the live service's use of that factory is pinned by
    /// <c>UIAutomationServiceTests.InteractAsync_toggle_names_the_unsupported_pattern_and_the_control</c>.
    /// </summary>
    [Fact]
    public async Task Interact_element_toggle_on_a_control_without_the_pattern_reaches_the_client_naming_it()
    {
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.InteractAsync("el_7", "toggle", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(WindowsMcp.Services.UiTree.UiRefusals.PatternNotSupported("TogglePattern", "Button", "Save"));

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(uia.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "interactelement"), new Dictionary<string, object?>
        {
            ["element_id"] = "el_7",
            ["action"] = "toggle",
        });

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().NotContain("An error occurred invoking", "the refusal is the answer, not a crash");
        text.Should().Contain("TogglePattern").And.Contain("Button").And.Contain("'Save'");
        uia.Verify(s => s.InteractAsync("el_7", "toggle", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>C-1 R4-7: and the commonest one of all — a file that is not there.</summary>
    [Fact]
    public async Task File_read_of_a_missing_file_reaches_the_client_naming_the_path()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();
        var missing = Path.Combine(Path.GetTempPath(), "wmcp-http-missing-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = await client.CallToolAsync(ToolName(tools, "fileread"), new Dictionary<string, object?>
        {
            ["path"] = missing,
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain(missing, "'An error occurred invoking file_read' does not say which file");
    }

    /// <summary>
    /// C-1 R4b-6: a refusal quotes what the caller sent, and R4-7 now sends those refusals back
    /// verbatim — so the size of the answer is whatever the caller made it. The filter caps a
    /// caller-facing message at 2 000 characters (<c>ToolErrors.MessageFor</c>): 3 000 characters
    /// of registry path is not an answer, it is the same path handed back.
    /// </summary>
    [Fact]
    public async Task A_refusal_that_quotes_a_huge_path_is_capped_before_it_reaches_the_client()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();
        // Twenty segments of 150 characters: each is inside the registry's 255-character limit, so
        // the key is simply not there — and "Registry path not found: HKCU\…" quotes all 3 019.
        var path = string.Join("\\", Enumerable.Repeat(new string('k', 150), 20));

        var result = await client.CallToolAsync(ToolName(tools, "registryget"), new Dictionary<string, object?>
        {
            ["hive"] = "HKCU",
            ["path"] = path,
        });

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("Registry path not found", "the answer is still the answer");
        text.Length.Should().BeLessThanOrEqualTo(2100,
            "the filter caps a caller-facing message at 2 000 characters, whatever the caller sent");
    }

    // ---- C-5 (R8): scrape, and the sampling round trip ----------------------------------------

    /// <summary>The fixed page every scrape test below is served by the mocked service.</summary>
    private static readonly ScrapeResult FixedScrape =
        new("http", "https://example.test/", "Example", 9, false, "page text");

    /// <summary>
    /// Replaces <c>IWebService</c> at the <c>BuildHttpApp</c> seam: what is under test here is the
    /// tool, the transport and the sampling round trip, not anyone's internet connection.
    /// </summary>
    private static Action<IServiceCollection> WithScrapeService()
    {
        var mock = new Mock<IWebService>();
        mock.Setup(s => s.ScrapeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedScrape);
        return services => services.AddSingleton(mock.Object);
    }

    /// <summary>
    /// C-5: the tool takes the SDK-bound <c>McpServer</c> as a parameter. The SDK binds it by type
    /// and must NOT advertise it to the client — a "server" property in the schema is a property a
    /// model would try to fill in.
    /// </summary>
    [Fact]
    public async Task Scrape_is_advertised_without_the_injected_server_and_without_a_required_url()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "scrape");

        ShouldAdvertise(properties, "url", "query", "source", "summarize", "max_chars", "window");
        properties.TryGetProperty("server", out _).Should().BeFalse(
            "the McpServer parameter is bound by the SDK, not sent by the client");
        required.Should().BeEmpty("source:'dom' reads the open page, so url is optional (C-5)");
    }

    /// <summary>
    /// C-5: <c>summarize:true</c> over HTTP is answered by the <b>transport</b>, not by the client's
    /// capabilities: <see cref="WindowsMcpHost.BuildHttpApp"/> runs Streamable HTTP stateless, so a
    /// per-request server has no session to carry a sampling request on. The note must say that and
    /// point at stdio, instead of blaming a client that was never asked.
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_without_a_sampling_client_returns_the_text_and_the_note()
    {
        await using var app = await Harness.StartAsync(configureServices: WithScrapeService());
        await using var client = await ConnectAsync(app.McpEndpoint);

        var result = await client.CallToolAsync(ToolName(await client.ListToolsAsync(), "scrape"),
            new Dictionary<string, object?>
            {
                ["url"] = "https://example.test/",
                ["summarize"] = true,
            });

        result.IsError.Should().NotBe(true);
        using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        doc.RootElement.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("Content").GetString().Should().Be("page text",
            "the page is never lost because the summary could not happen");
        var note = doc.RootElement.GetProperty("Note").GetString();
        note.Should().Contain("stateless", "the reason is the transport's statelessness, not the client");
        note.Should().Contain("HTTP", "and the caller is told which transport it is on");
        note.Should().Contain("stdio", "with the transport that CAN summarise named as the way out");
        note.Should().NotContain("capability",
            "this client declared nothing either way; blaming its capabilities would send the "
            + "caller looking in the wrong place");
    }

    /// <summary>
    /// C-5: the honest HTTP pin. A client that DOES declare sampling and DOES install a handler
    /// still gets the text back with the stateless note, and its handler is never invoked — the
    /// stateless server never asks. Also the DI proof for the seam: <c>WebTools</c>'s optional
    /// <c>TransportOptions</c> parameter resolves from the host's registration, which is the only
    /// reason the tool knows it is on HTTP at all.
    /// <para>
    /// The real <c>McpServer.SampleAsync</c> round trip is proven in
    /// <c>StreamTransportTests</c> over a session-keeping transport; it cannot be proven here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_over_http_never_asks_even_a_client_that_can_sample()
    {
        var requests = new List<CreateMessageRequestParams>();

        await using var app = await Harness.StartAsync(configureServices: WithScrapeService());

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = app.McpEndpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
        });
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            Capabilities = new ClientCapabilities { Sampling = new SamplingCapability() },
            Handlers = new McpClientHandlers
            {
                SamplingHandler = (request, _, _) =>
                {
                    requests.Add(request!);
                    return ValueTask.FromResult(new CreateMessageResult
                    {
                        Content = [new TextContentBlock { Text = "canned summary" }],
                        Model = "canned-model",
                        Role = Role.Assistant,
                    });
                },
            },
        });

        var result = await client.CallToolAsync(ToolName(await client.ListToolsAsync(), "scrape"),
            new Dictionary<string, object?>
            {
                ["url"] = "https://example.test/",
                ["query"] = "what is the price",
                ["summarize"] = true,
            });

        result.IsError.Should().NotBe(true);
        using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        doc.RootElement.GetProperty("Summarized").GetBoolean().Should().BeFalse(
            "a stateless server has no stream for the reply to come back on, whatever the client can do");
        doc.RootElement.GetProperty("Model").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("Content").GetString().Should().Be("page text");
        doc.RootElement.GetProperty("Note").GetString().Should().Contain("stateless");

        requests.Should().BeEmpty(
            "the client is never asked over HTTP - a request it could not answer would just hang the call");
    }

}


/// <summary>
/// The one A-7 test that captures the real screen through the real HTTP host. Split out of
/// <see cref="HttpTransportTests"/> because that class is <c>Category=Integration</c> and a
/// vstest <c>Category!=UIAutomation</c> filter does not exclude a test that also carries
/// another Category value.
/// <para>
/// <c>Graphics.CopyFromScreen</c> needs an interactive desktop session (see the note on
/// <c>ScreenshotServiceTests</c>), so this fails headless — run it from an interactive session.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
public class HttpTransportScreenshotImageTests
{
    [Fact]
    public async Task Screenshot_returns_an_image_content_block_over_http()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var screenshot = HttpTransportTests.ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "0,0,64,48",   // small: this is a wiring proof, not a capture-quality test
            ["format"] = "png",
        });

        result.IsError.Should().NotBe(true);

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.Data.Length.Should().BeGreaterThan(0, "the base64 image must survive the JSON-RPC round trip");
        image.MimeType.Should().Be("image/png");
        image.DecodedData.ToArray().Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "PNG magic bytes");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(64);
        meta.RootElement.GetProperty("height").GetInt32().Should().Be(48);
        meta.RootElement.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    /// <summary>
    /// A-7 flipped the inline default to JPEG. Every mocked tool test hands back bytes a human
    /// wrote, so this is the only thing that proves the real Skia encode produces JPEG for the
    /// default path an agent loop actually takes (no <c>format</c> argument).
    /// </summary>
    [Fact]
    public async Task Screenshot_default_format_is_jpeg_over_http()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var screenshot = HttpTransportTests.ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "0,0,64,48",   // no 'format': exercise the default
        });

        result.IsError.Should().NotBe(true);

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.MimeType.Should().Be("image/jpeg", "inline output defaults to jpeg (A-7)");
        image.DecodedData.ToArray().Take(3).Should().Equal(new byte[] { 0xFF, 0xD8, 0xFF }, "JPEG SOI marker");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("format").GetString().Should().Be("jpeg");
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(64);
    }

    /// <summary>
    /// A-10 end to end: the tool argument, the real <c>ScreenshotService</c>, the real compositor
    /// and the metadata, over the real transport. Every other backend test replaces one of those
    /// with a mock (<c>ScreenToolsTests</c>, <c>HttpTransportTests</c>) or with the
    /// <c>WgcFrameSource</c> seam (<c>ScreenshotFrameSourceTests</c>) — this is the only one where
    /// a client asking for <c>backend:"wgc"</c> gets a picture the compositor actually produced.
    /// </summary>
    [Fact]
    public async Task Screenshot_backend_wgc_captures_through_the_compositor_over_http()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var screenshot = HttpTransportTests.ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "0,0,64,48",   // small: this is a wiring proof, not a capture-quality test
            ["format"] = "png",
            ["backend"] = "wgc",
        });

        result.IsError.Should().NotBe(true,
            "a machine that supports WGC must serve a capture asked for by name, not refuse it");

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.DecodedData.ToArray().Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "PNG magic bytes");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("backend").GetString().Should().Be("wgc",
            "the frame came from the compositor and the metadata says so, through the whole stack");
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(64);
        meta.RootElement.GetProperty("height").GetInt32().Should().Be(48);
    }
}


/// <summary>
/// A-5 phase 1 (R7): the one DOM test that drives the REAL <c>UIAutomationService</c> against a
/// real Edge window through the real HTTP host — the non-mocked sibling of
/// <see cref="HttpTransportTests.Snapshot_use_dom_over_http_forwards_the_flag_and_returns_the_pages"/>,
/// which proves only that the flag and the DTO survive the wire.
/// <para>
/// Split out of <see cref="HttpTransportTests"/> for the same reason as
/// <see cref="HttpTransportScreenshotImageTests"/>: that class is <c>Category=Integration</c>, and
/// a <c>Category!=UIAutomation</c> filter does not exclude a test that carries both values.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(EdgeCollection.Name)]
public class HttpTransportDomSnapshotTests
{
    private readonly EdgeFixture _edge;

    public HttpTransportDomSnapshotTests(EdgeFixture edge) => _edge = edge;

    [Fact]
    public async Task Snapshot_use_dom_over_http_returns_the_real_pages_section()
    {
        if (!_edge.Available) return;   // no Edge on this machine: nothing to assert

        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var snapshot = HttpTransportTests.SnapshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(snapshot, new Dictionary<string, object?>
        {
            ["scope"] = "window",
            ["window"] = _edge.WindowTitle,
            ["use_dom"] = true,
            ["format"] = "json",
        });

        result.IsError.Should().NotBe(true);
        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject.Text;
        using var doc = JsonDocument.Parse(text);

        var pages = doc.RootElement.GetProperty("Pages");
        pages.GetArrayLength().Should().Be(1, "one browser window was in scope");
        var page = pages[0];
        page.GetProperty("Title").GetString().Should().Be(EdgeFixture.PageTitle);
        page.GetProperty("Url").GetString().Should().StartWith(_edge.BaseUrl);
        page.GetProperty("DocumentId").GetString().Should().NotBeNullOrWhiteSpace();
        page.GetProperty("Text").EnumerateArray().Select(t => t.GetString())
            .Should().Contain("Probe heading", "the page's visible text crosses the transport intact");
    }
}
#pragma warning restore MCP9005
