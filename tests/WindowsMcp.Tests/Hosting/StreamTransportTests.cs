using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

// MCP9005: the SDK marks the Sampling feature obsolete as of specification version 2026-07-28
// (SEP-2577). C-5's summary path exists only through client-side sampling and this file is where
// the real round trip runs; see src/WindowsMcp/Tools/ISamplingClient.cs.
#pragma warning disable MCP9005

/// <summary>
/// C-5 (roadmap R8): the sampling round trip, in process, over a <b>session-keeping</b> transport.
/// <para>
/// <see cref="HttpTransportTests"/> cannot prove this: <c>BuildHttpApp</c> runs Streamable HTTP
/// stateless (a fresh <c>McpServer</c> per request), so that server never learns what the client
/// declared at initialize and has no stream for a <c>sampling/createMessage</c> reply to return
/// on — the HTTP tests pin the honest fallback instead. Everything except the transport is the
/// production wiring: <c>AddWindowsMcp</c> builds the services, the tool discovery, the C-7
/// annotations and the <c>ToolErrors</c> call filter, and the server is created from the same
/// <c>IOptions&lt;McpServerOptions&gt;</c> the stdio host uses. A pair of
/// <see cref="Pipe"/>s stands in for stdin/stdout.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class StreamTransportTests
{
    /// <summary>The fixed page the mocked <c>IWebService</c> serves; nothing here touches the internet.</summary>
    private static readonly ScrapeResult FixedScrape =
        new("http", "https://example.test/", "Example", 9, false, "page text");

    private static Action<IServiceCollection> WithScrapeService()
    {
        var mock = new Mock<IWebService>();
        mock.Setup(s => s.ScrapeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FixedScrape);
        return services => services.AddSingleton(mock.Object);
    }

    /// <summary>
    /// A real <see cref="McpServer"/> and a real <see cref="McpClient"/> talking JSON-RPC to each
    /// other over two in-memory pipes, with the production service graph behind the server.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly McpServer _server;
        private readonly Task _run;
        private readonly CancellationTokenSource _cts;

        public McpClient Client { get; }

        private Harness(IHost host, McpServer server, Task run, McpClient client, CancellationTokenSource cts)
        {
            _host = host;
            _server = server;
            _run = run;
            Client = client;
            _cts = cts;
        }

        public static async Task<Harness> StartAsync(
            McpClientOptions? clientOptions = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();   // the test output is not the server's stderr
            // The stdio configuration: TransportOptions(Stateless: false), so the tool asks.
            builder.AddWindowsMcp(new ServerOptions(TransportKind.Stdio, "127.0.0.1", 0, null, null));
            configureServices?.Invoke(builder.Services);
            var host = builder.Build();

            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            // Exactly what the stdio host resolves: ServerInfo, the discovered tools with their
            // C-7 annotations, and the caller-facing error filter.
            var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;

            var toServer = new Pipe();
            var toClient = new Pipe();

            var server = McpServer.Create(
                new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(),
                    "stream-transport-test", loggerFactory),
                options, loggerFactory, host.Services);

            var cts = new CancellationTokenSource();
            var run = server.RunAsync(cts.Token);

            try
            {
                var client = await McpClient.CreateAsync(
                    new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), loggerFactory),
                    clientOptions,
                    loggerFactory,
                    cts.Token);
                return new Harness(host, server, run, client, cts);
            }
            catch
            {
                await cts.CancelAsync();
                await server.DisposeAsync();
                host.Dispose();
                cts.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _cts.CancelAsync();
            await _server.DisposeAsync();
            try { await _run; } catch (OperationCanceledException) { /* the harness ended the session */ }
            catch (IOException) { /* the pipes were torn down under the reader */ }
            _host.Dispose();
            _cts.Dispose();
        }
    }

    private static McpClientOptions Sampling(Action<CreateMessageRequestParams> record) => new()
    {
        Capabilities = new ClientCapabilities { Sampling = new SamplingCapability() },
        Handlers = new McpClientHandlers
        {
            SamplingHandler = (request, _, _) =>
            {
                record(request!);
                return ValueTask.FromResult(new CreateMessageResult
                {
                    Content = [new TextContentBlock { Text = "canned summary" }],
                    Model = "canned-model",
                    Role = Role.Assistant,
                });
            },
        },
    };

    private static async Task<JsonElement> ScrapeAsync(Harness harness, IReadOnlyDictionary<string, object?> arguments)
    {
        var tools = await harness.Client.ListToolsAsync();
        var name = tools.Single(t => t.Name.Replace("_", "").Equals("scrape", StringComparison.OrdinalIgnoreCase)).Name;

        var result = await harness.Client.CallToolAsync(name, arguments);

        result.IsError.Should().NotBe(true, "the call itself succeeded; a failed summary is a Note, not an error");
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// The harness is the real wiring, not a hand-rolled echo server: the tools the client sees
    /// are the discovered ones, with the annotations <c>C-7</c> put on them.
    /// </summary>
    [Fact]
    public async Task The_stream_harness_serves_the_real_tool_inventory()
    {
        await using var harness = await Harness.StartAsync();

        var tools = await harness.Client.ListToolsAsync();

        tools.Should().HaveCount(69, "the same inventory ToolInventoryTests counts, discovered from the assembly");
        var scrape = tools.Single(t => t.Name.Replace("_", "").Equals("scrape", StringComparison.OrdinalIgnoreCase));
        scrape.ProtocolTool.Annotations.Should().NotBeNull();
        scrape.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue("scrape only reads");
        scrape.ProtocolTool.Annotations.OpenWorldHint.Should().BeTrue("it reaches the internet");
    }

    /// <summary>
    /// E-1 (R2): the stdio-shaped path carries the same instructions as HTTP. The harness builds
    /// the server from the <c>IOptions&lt;McpServerOptions&gt;</c> the stdio host resolves, so this
    /// fails if the instructions were set anywhere but the shared <c>AddWindowsMcp</c> wiring.
    /// </summary>
    [Fact]
    public async Task Initialize_over_the_stdio_wiring_carries_the_server_instructions()
    {
        await using var harness = await Harness.StartAsync();

        harness.Client.ServerInstructions.Should().NotBeNullOrEmpty(
            "AddWindowsMcp sets the instructions, so the stdio transport sends them in initialize");
        harness.Client.ServerInstructions.Should().Be(ServerInstructions.Text);
    }

    /// <summary>
    /// E-1 GREEN: the instructions tell the model that only "An error occurred invoking ..." is an
    /// unexpected fault. That wording is the SDK's, not ours, so it is pinned against what an
    /// unexpected fault really reads as over the production wiring: a service throwing a type
    /// <c>ToolErrors.IsCallerFacing</c> rejects. An SDK upgrade that rewords the mask, or a text
    /// edit that misquotes it, turns this red. get_element is read-only and not a driving tool, so
    /// this harness (control indicator on) draws nothing and registers no hotkey.
    /// </summary>
    [Fact]
    public async Task An_unexpected_fault_reads_as_the_mask_the_instructions_quote()
    {
        var quoted = System.Text.RegularExpressions.Regex.Match(ServerInstructions.Text, "\"([^\"]*) \\.\\.\\.\"");
        quoted.Success.Should().BeTrue("the instructions quote the mask with its tool name elided");
        var mask = quoted.Groups[1].Value;
        mask.Should().StartWith("An error occurred");

        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NullReferenceException("a bug, never shown to the caller"));
        await using var harness = await Harness.StartAsync(configureServices: services => services.AddSingleton(uia.Object));
        var tools = await harness.Client.ListToolsAsync();
        var name = tools.Single(t => t.Name.Replace("_", "").Equals("getelement", StringComparison.OrdinalIgnoreCase)).Name;
        DrivingTools.Names.Should().NotContain(name, "the call must not raise the control indicator");

        var result = await harness.Client.CallToolAsync(name, new Dictionary<string, object?>
        {
            ["element_id"] = "el_1",
        });

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().StartWith(mask, "the model is told to recognise an unexpected fault by exactly this text");
        text.Should().NotContain("a bug, never shown", "the mask hides the exception message");
        uia.Verify(s => s.GetElementAsync("el_1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// C-5: the whole summary path over the wire — the client declares sampling, the tool asks it,
    /// the client's model answers, and the answer is what the caller gets. This is the only test
    /// that runs the real <c>McpServerSampling</c> adapter (<c>McpServer.SampleAsync</c>) rather
    /// than the faked <c>ISamplingClient</c>.
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_asks_the_client_and_returns_its_summary()
    {
        var requests = new List<CreateMessageRequestParams>();
        await using var harness = await Harness.StartAsync(
            Sampling(requests.Add), WithScrapeService());

        var result = await ScrapeAsync(harness, new Dictionary<string, object?>
        {
            ["url"] = "https://example.test/",
            ["query"] = "what is the price",
            ["summarize"] = true,
        });

        result.GetProperty("Summarized").GetBoolean().Should().BeTrue();
        result.GetProperty("Model").GetString().Should().Be("canned-model",
            "the model name comes from the client's reply, not from anything the server knows");
        result.GetProperty("Content").GetString().Should().Be("canned summary");
        result.GetProperty("Note").ValueKind.Should().Be(JsonValueKind.Null, "nothing was refused");
        result.GetProperty("Chars").GetInt32().Should().Be(9, "Chars still describes the PAGE, not the summary");

        var sampled = requests.Should().ContainSingle().Subject;
        sampled.SystemPrompt.Should().Contain("what is the price", "the query focuses the summary");
        sampled.Messages.Should().ContainSingle().Which.Content
            .Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("page text", "the page the service returned is what crossed the wire");
    }

    /// <summary>
    /// C-5: the same harness, a client that declared nothing — the path Claude Code actually runs.
    /// Over a session-keeping transport the server DOES know what the client can do, so the note
    /// names the client's <em>capability</em>, not the stateless transport HTTP blames.
    /// <para>
    /// A client with no <c>SamplingHandler</c> is the only way to be a non-sampling client: the SDK
    /// derives the declared capability from the installed handler, so a handler present with
    /// <c>Capabilities.Sampling</c> left null still initializes as sampling-capable (observed on
    /// ModelContextProtocol 2.2.0). That the note below is the <em>capability</em> note is itself
    /// the proof the tool never asked: <c>McpServer.SampleAsync</c> against a client that did not
    /// declare it throws <c>InvalidOperationException</c>, which <c>ToolErrors</c> would surface as
    /// an <c>IsError</c> result, and an <c>McpException</c> would produce a different note.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_without_the_capability_returns_the_text_and_names_the_capability()
    {
        await using var harness = await Harness.StartAsync(
            new McpClientOptions { Capabilities = new ClientCapabilities() },
            WithScrapeService());

        var result = await ScrapeAsync(harness, new Dictionary<string, object?>
        {
            ["url"] = "https://example.test/",
            ["summarize"] = true,
        });

        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text", "the page is never lost");
        result.GetProperty("Model").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("Note").GetString().Should().Be(
            "summarize:true was ignored: this client did not declare the sampling capability, so "
            + "the content is returned as is",
            "over stdio the capability check decides, so the note names the capability - not the transport");
    }

    /// <summary>
    /// C-5: <c>summarize:false</c> (the default) never touches the client, whatever it declared —
    /// sampling costs the client a model call.
    /// </summary>
    [Fact]
    public async Task Scrape_without_summarize_never_samples_even_a_capable_client()
    {
        var requests = new List<CreateMessageRequestParams>();
        await using var harness = await Harness.StartAsync(
            Sampling(requests.Add), WithScrapeService());

        var result = await ScrapeAsync(harness, new Dictionary<string, object?>
        {
            ["url"] = "https://example.test/",
        });

        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text");
        result.GetProperty("Note").ValueKind.Should().Be(JsonValueKind.Null, "nothing was refused");
        requests.Should().BeEmpty("the default is not to bill the client for a model call");
    }
}
#pragma warning restore MCP9005
