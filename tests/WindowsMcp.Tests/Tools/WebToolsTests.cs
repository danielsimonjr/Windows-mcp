using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Tools;

// MCP9005: the SDK marks the Sampling feature obsolete as of specification version 2026-07-28
// (SEP-2577). C-5 summarises only through client-side sampling; see the note on
// src/WindowsMcp/Tools/ISamplingClient.cs.
#pragma warning disable MCP9005

/// <summary>
/// C-5 (roadmap R8): the <c>scrape</c> tool. Every collaborator is mocked here — the service, the
/// DOM walk, the window inventory and the sampling seam — so what is under test is the tool's own
/// contract: which refusals fire before anything is fetched or walked, which call is made with
/// which arguments, and what the JSON says.
/// </summary>
[Trait("Category", "Unit")]
public class WebToolsTests
{
    private const string TopHint = "Reached top of the page; scroll down to see more.";

    /// <summary>
    /// A document that does not scroll vertically, so <see cref="WindowsMcp.Services.DomPage"/>
    /// adds no hint and the rendered text is exactly the page's lines. <c>SnapshotFixtures.Page</c>
    /// substitutes a top-of-page <c>ScrollInfo</c> for a null one, so "no hint" has to be asked
    /// for explicitly — passing <c>scroll: null</c> would silently render the top hint.
    /// </summary>
    private static readonly ScrollInfo NoScroll = new(0, 0, false, false);

    /// <summary>The sampling seam, recording what it was asked and answering what it was told to.</summary>
    private sealed class FakeSampling : ISamplingClient
    {
        public bool Supported { get; init; }
        public CreateMessageResult? Reply { get; init; }
        public Exception? Throws { get; init; }
        public List<CreateMessageRequestParams> Requests { get; } = [];

        /// <summary>F10: the token the tool handed the client, one per call.</summary>
        public List<CancellationToken> Tokens { get; } = [];

        /// <summary>
        /// F10: a client that never answers, as the tool's own deadline would eventually see it —
        /// a cancellation carrying the token the TOOL supplied, not the caller's.
        /// </summary>
        public bool NeverAnswers { get; init; }

        public Task<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken ct)
        {
            Requests.Add(request);
            Tokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            if (NeverAnswers) throw new OperationCanceledException(ct);
            if (Throws is not null) throw Throws;
            return Task.FromResult(Reply ?? Canned());
        }

        public static CreateMessageResult Canned(params string[] texts)
        {
            var blocks = (texts.Length == 0 ? new[] { "canned summary" } : texts)
                .Select(t => (ContentBlock)new TextContentBlock { Text = t })
                .ToList();
            return new CreateMessageResult { Content = blocks, Model = "canned-model", Role = Role.Assistant };
        }
    }

    private static FakeSampling NoSampling() => new() { Supported = false };
    private static FakeSampling WithSampling(CreateMessageResult? reply = null, Exception? throws = null)
        => new() { Supported = true, Reply = reply, Throws = throws };

    private static Mock<IWebService> Web(ScrapeResult? result = null)
    {
        var web = new Mock<IWebService>();
        web.Setup(s => s.ScrapeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(result ?? new ScrapeResult("http", "https://example.test/", "Example", 9, false, "page text"));
        return web;
    }

    private static Mock<IUIAutomationService> Uia(params SnapshotPage[] pages)
    {
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Result(pages: pages));
        return uia;
    }

    /// <summary>
    /// F2: the same walk, stopped by the element budget. <c>SnapshotResult.Truncated</c> is how A-2
    /// says "there was more of this window than I was allowed to look at" — for a page that means
    /// the text below the cut is missing, and the caller cannot see that from the text.
    /// </summary>
    private static Mock<IUIAutomationService> UiaCutByTheBudget(int elementLimit, params SnapshotPage[] pages)
    {
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Result(truncated: true, elementLimit: elementLimit, pages: pages));
        return uia;
    }

    /// <summary>
    /// A browser window the DOM walk found no page document under: A-5 reports the reason in the
    /// page's <c>Note</c> and leaves <c>DocumentId</c> null.
    /// </summary>
    private static SnapshotPage NoDocument(string note = "no page document found under this window")
        => Page(documentId: null, title: null, url: null, scroll: NoScroll, text: ["some window text"], note: note);

    private static Mock<IWindowService> Windows(params WindowInfo[] windows)
    {
        var service = new Mock<IWindowService>();
        service.Setup(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(windows);
        return service;
    }

    /// <param name="transport">
    /// C-5: null is the production default for a tool built without one — stdio, where the session
    /// lives as long as the process and the client's declared capability decides.
    /// </param>
    private static WebTools Tools(
        Mock<IWebService>? web = null, Mock<IUIAutomationService>? uia = null,
        Mock<IWindowService>? windows = null, TransportOptions? transport = null)
        => new(web?.Object ?? new Mock<IWebService>().Object,
               uia?.Object ?? new Mock<IUIAutomationService>().Object,
               windows?.Object ?? new Mock<IWindowService>().Object,
               transport);

    private static Task<string> Scrape(
        WebTools tools, ISamplingClient sampling, string? url = null, string? query = null,
        string source = "http", bool summarize = false, int max_chars = 100000, string? window = null,
        CancellationToken ct = default)
        => tools.ScrapeAsync(url, query, source, summarize, max_chars, window, sampling, ct);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // ---- refusals, before anything is fetched or walked ---------------------------------------

    private static async Task RefusesNaming(
        string parameter, string? url = null, string? query = null, string source = "http",
        bool summarize = false, int max_chars = 100000, string? window = null)
    {
        var web = Web();
        var uia = Uia();
        var windows = Windows();

        Func<Task> act = () => Scrape(Tools(web, uia, windows), NoSampling(),
            url, query, source, summarize, max_chars, window);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage($"*{parameter}*");
        web.VerifyNoOtherCalls();
        uia.VerifyNoOtherCalls();
        windows.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ftp")]
    [InlineData("")]
    [InlineData("web")]
    public async Task Scrape_refuses_an_unknown_source(string source)
        => await RefusesNaming("source", url: "https://example.test/", source: source);

    [Theory]
    [InlineData("HTTP")]
    [InlineData("Dom")]
    public async Task Scrape_accepts_the_sources_in_any_case(string source)
    {
        var page = Page(text: ["Probe heading"], scroll: null);
        var uia = Uia(page);
        var tools = Tools(Web(), uia,
            Windows(Window(title: "A5 Probe Page", process: "msedge", isBrowser: true)));

        var json = await Scrape(tools, NoSampling(),
            url: source.Equals("HTTP", StringComparison.OrdinalIgnoreCase) ? "https://example.test/" : null,
            source: source);

        Json(json).GetProperty("Source").GetString().Should().Be(source.ToLowerInvariant(),
            "the source is matched case-insensitively and reported in its canonical lower-case form");
    }

    [Theory]
    [InlineData(0)]         // 0 is not "all"
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public async Task Scrape_refuses_a_max_chars_outside_the_range(int maxChars)
        => await RefusesNaming("max_chars", url: "https://example.test/", max_chars: maxChars);

    [Fact]
    public async Task Scrape_refuses_http_without_a_url()
        => await RefusesNaming("url", url: null, source: "http");

    [Fact]
    public async Task Scrape_refuses_http_with_a_window()
        => await RefusesNaming("window", url: "https://example.test/", source: "http", window: "Edge");

    [Fact]
    public async Task Scrape_refuses_dom_with_a_url()
        => await RefusesNaming("url", url: "https://example.test/", source: "dom");

    [Fact]
    public async Task Scrape_refuses_a_query_without_summarize()
        => await RefusesNaming("query", url: "https://example.test/", query: "what is the price", summarize: false);

    /// <summary>
    /// C-5: the refusals run in the order the design note lists them — source, then max_chars, then
    /// the url/window pairing, then query. A call that breaks several rules is told about the most
    /// fundamental one first: "unknown source" is why nothing else could be checked, and reporting
    /// <c>query</c> instead would send the caller to fix the wrong thing.
    /// </summary>
    [Theory]
    // source loses to nothing: it is checked first.
    [InlineData("source", "ftp", 100000, "https://example.test/", "what is the price", null)]
    [InlineData("source", "ftp", 0, "https://example.test/", null, null)]
    [InlineData("source", "ftp", 100000, null, null, null)]
    // then max_chars, ahead of the url/window pairing and the query.
    [InlineData("max_chars", "http", 0, null, "what is the price", null)]
    [InlineData("max_chars", "dom", -1, "https://example.test/", null, null)]
    // then the url/window pairing, ahead of the query.
    [InlineData("url", "http", 100000, null, "what is the price", null)]
    [InlineData("url", "dom", 100000, "https://example.test/", "what is the price", null)]
    [InlineData("window", "http", 100000, "https://example.test/", "what is the price", "Edge")]
    public async Task Scrape_names_the_first_broken_rule_when_several_are_broken(
        string parameter, string source, int maxChars, string? url, string? query, string? window)
        => await RefusesNaming(parameter, url: url, query: query, source: source,
            summarize: false, max_chars: maxChars, window: window);

    // ---- source: http --------------------------------------------------------------------------

    [Fact]
    public async Task Scrape_http_forwards_the_url_and_the_cap_and_reports_the_service_result()
    {
        var web = Web(new ScrapeResult("http", "https://example.test/moved", "Example", 42, true, "page text"));
        var tools = Tools(web);

        var json = await Scrape(tools, NoSampling(), url: "https://example.test/");

        var result = Json(json);
        result.GetProperty("Source").GetString().Should().Be("http");
        result.GetProperty("Url").GetString().Should().Be("https://example.test/moved",
            "the URL the content came from after redirects, not the one the caller typed");
        result.GetProperty("Title").GetString().Should().Be("Example");
        result.GetProperty("Chars").GetInt32().Should().Be(42);
        result.GetProperty("Truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("Content").GetString().Should().Be("page text");
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();

        web.Verify(s => s.ScrapeAsync("https://example.test/", 100000, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// E-1 (review R-F4): the instructions name <c>scrape</c> with source dom as a call that
    /// replaces snapshot ids, so source http must not: a successful fetch never touches UIA or the
    /// window inventory, and the caller's snapshot ids survive it.
    /// </summary>
    [Fact]
    public async Task Scrape_http_never_walks_the_desktop()
    {
        var web = Web();
        var uia = Uia();
        var windows = Windows();

        await Scrape(Tools(web, uia, windows), NoSampling(), url: "https://example.test/");

        web.Verify(s => s.ScrapeAsync("https://example.test/", 100000, It.IsAny<CancellationToken>()), Times.Once);
        uia.VerifyNoOtherCalls();
        windows.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]             // the low end of the range, exactly
    [InlineData(5000)]
    [InlineData(1_000_000)]     // the high end, exactly
    public async Task Scrape_http_forwards_max_chars_to_the_service(int maxChars)
    {
        var web = Web();

        await Scrape(Tools(web), NoSampling(), url: "https://example.test/", max_chars: maxChars);

        web.Verify(s => s.ScrapeAsync("https://example.test/", maxChars, It.IsAny<CancellationToken>()), Times.Once,
            "the cap is the service's job for the http source - the tool must not cut the text twice");
    }

    [Fact]
    public async Task Scrape_http_passes_the_cancellation_token_through()
    {
        using var cts = new CancellationTokenSource();
        var web = Web();

        await Scrape(Tools(web), NoSampling(), url: "https://example.test/", ct: cts.Token);

        web.Verify(s => s.ScrapeAsync("https://example.test/", 100000, cts.Token), Times.Once);
    }

    // ---- summarize: true -----------------------------------------------------------------------

    [Fact]
    public async Task Scrape_summarize_without_the_client_capability_returns_the_text_and_says_why()
    {
        var sampling = NoSampling();

        var json = await Scrape(Tools(Web()), sampling, url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text", "the page is never lost");
        result.GetProperty("Model").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("Note").GetString().Should().Be(
            "summarize:true was ignored: this client did not declare the sampling capability, so "
            + "the content is returned as is");
        sampling.Requests.Should().BeEmpty("a client that cannot sample must never be asked to");
    }

    [Fact]
    public async Task Scrape_summarize_sends_the_content_and_returns_the_models_answer()
    {
        var sampling = WithSampling();

        var json = await Scrape(Tools(Web()), sampling,
            url: "https://example.test/", query: "what is the price", summarize: true);

        var request = sampling.Requests.Should().ContainSingle().Subject;
        request.Messages.Should().ContainSingle().Which.Role.Should().Be(Role.User);
        request.Messages[0].Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("page text", "the (already capped) content is what the model is given");
        request.SystemPrompt.Should().Contain("what is the price", "the query focuses the summary");

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeTrue();
        result.GetProperty("Model").GetString().Should().Be("canned-model");
        result.GetProperty("Content").GetString().Should().Be("canned summary");
        result.GetProperty("Note").ValueKind.Should().Be(JsonValueKind.Null, "nothing was refused");
    }

    [Fact]
    public async Task Scrape_summarize_keeps_the_pages_size_not_the_summarys()
    {
        var web = Web(new ScrapeResult("http", "https://example.test/", "Example", 4200, true, "page text"));

        var json = await Scrape(Tools(web), WithSampling(), url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Chars").GetInt32().Should().Be(4200, "Chars describes the PAGE, not the summary");
        result.GetProperty("Truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Scrape_summarize_joins_the_replys_text_blocks()
    {
        var sampling = WithSampling(FakeSampling.Canned("part one", "part two"));

        var json = await Scrape(Tools(Web()), sampling, url: "https://example.test/", summarize: true);

        Json(json).GetProperty("Content").GetString().Should().Be("part one\npart two");
    }

    [Fact]
    public async Task Scrape_summarize_of_a_blank_page_is_never_sent()
    {
        var web = Web(new ScrapeResult("http", "https://example.test/", null, 0, false, "   "));
        var sampling = WithSampling();

        var json = await Scrape(Tools(web), sampling, url: "https://example.test/", summarize: true);

        sampling.Requests.Should().BeEmpty("there is nothing to summarise; the client is not billed for it");
        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Note").GetString().Should().Be("nothing to summarise: the page has no text");
    }

    [Fact]
    public async Task Scrape_summarize_with_a_reply_that_has_no_text_returns_the_page_and_says_so()
    {
        var reply = new CreateMessageResult { Content = [], Model = "canned-model", Role = Role.Assistant };

        var json = await Scrape(Tools(Web()), WithSampling(reply), url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text",
            "the page is not lost because the summary failed");
        result.GetProperty("Note").GetString().Should().ContainEquivalentOf("no text");
    }

    [Fact]
    public async Task Scrape_summarize_that_the_client_refuses_returns_the_page_and_the_reason()
    {
        var sampling = WithSampling(throws: new McpException("client declined the sampling request"));

        var json = await Scrape(Tools(Web()), sampling, url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text");
        result.GetProperty("Note").GetString().Should().Contain("client declined the sampling request",
            "the caller has to be able to tell a refusal from an empty page");
    }

    [Fact]
    public async Task Scrape_summarize_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Scrape(Tools(Web()), WithSampling(),
            url: "https://example.test/", summarize: true, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled call is not a failed summary - it is not an answer at all");
    }

    // ---- summarize over a stateless transport ---------------------------------------------------

    private const string StatelessNote =
        "summarize:true was ignored: this server is running over its stateless HTTP transport, which keeps no "
        + "session to carry a sampling request to the client; run the server over stdio for summaries. The "
        + "content is returned as is";

    private const string NoCapabilityNote =
        "summarize:true was ignored: this client did not declare the sampling capability, so the content is returned as is";

    private static readonly TransportOptions Stateless = new(Stateless: true);

    /// <summary>
    /// C-5: over the stateless HTTP transport a per-request server has no session for the reply to
    /// come back on, so the client is <b>never asked</b> — even one that can sample. The note names
    /// the transport and the way out, instead of blaming the client for something it did declare.
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_over_a_stateless_transport_never_asks_and_says_why()
    {
        var sampling = WithSampling();

        var json = await Scrape(Tools(Web(), transport: Stateless), sampling,
            url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text", "the page is never lost");
        result.GetProperty("Model").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("Note").GetString().Should().Be(StatelessNote);
        sampling.Requests.Should().BeEmpty(
            "the seam is not consulted at all: a request the transport cannot carry would hang the call");
    }

    [Fact]
    public async Task Scrape_summarize_over_a_stateless_transport_does_not_blame_the_client()
    {
        var note = Json(await Scrape(Tools(Web(), transport: Stateless), NoSampling(),
            url: "https://example.test/", summarize: true)).GetProperty("Note").GetString();

        note.Should().Be(StatelessNote,
            "even a client that declared nothing gets the transport's reason - the capability was "
            + "never read, so naming it would send the caller looking in the wrong place");
        note.Should().NotBe(NoCapabilityNote);
    }

    [Fact]
    public async Task Scrape_over_a_stateless_transport_without_summarize_carries_no_note()
    {
        var json = await Scrape(Tools(Web(), transport: Stateless), NoSampling(), url: "https://example.test/");

        Json(json).GetProperty("Note").ValueKind.Should().Be(JsonValueKind.Null,
            "nothing was refused: the transport only matters when a summary was asked for");
    }

    [Fact]
    public async Task Scrape_summarize_with_the_stdio_transport_option_behaves_like_no_option_at_all()
    {
        var sampling = WithSampling();

        var json = await Scrape(Tools(Web(), transport: TransportOptions.Stdio), sampling,
            url: "https://example.test/", summarize: true);

        Json(json).GetProperty("Summarized").GetBoolean().Should().BeTrue(
            "TransportOptions.Stdio is the same session-keeping default a tool built without one assumes");
        sampling.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// C-5 + F2: a walk the element budget cut short and a summary that could not happen produce
    /// <b>two</b> reasons, and the caller has to be able to read both. These are the two notes that
    /// can genuinely co-occur — a page with no document is now a refusal (F3), so it never reaches
    /// the summary step to have a second note appended to it.
    /// </summary>
    [Fact]
    public async Task Scrape_joins_the_budget_note_with_the_stateless_note_readably()
    {
        var page = Page(text: ["some page text"], scroll: NoScroll);

        var json = await Scrape(Tools(Web(), UiaCutByTheBudget(500, page), Windows(), transport: Stateless),
            WithSampling(), source: "dom", window: "Edge", summarize: true);

        var note = Json(json).GetProperty("Note").GetString();
        note.Should().Contain("--max-tree-elements", "the walk's own reason is still there");
        note.Should().EndWith(StatelessNote, "and the summary's reason follows it");
        note.Should().Contain(". " + StatelessNote,
            "the first reason gains the full stop it lacked and the second follows as a sentence - "
            + "not concatenated into one run-on line");
        note.Should().NotStartWith(StatelessNote,
            "the reason the caller has an incomplete page comes first; the summary is the lesser problem");
    }

    [Fact]
    public async Task Scrape_joins_the_budget_note_with_the_missing_capability_note()
    {
        var page = Page(text: ["some page text"], scroll: NoScroll);

        var json = await Scrape(Tools(Web(), UiaCutByTheBudget(500, page), Windows()), NoSampling(),
            source: "dom", window: "Edge", summarize: true);

        var note = Json(json).GetProperty("Note").GetString();
        note.Should().Contain("--max-tree-elements");
        note.Should().EndWith(NoCapabilityNote);
        note.Should().Contain(". " + NoCapabilityNote);
    }

    [Fact]
    public async Task Scrape_joins_the_budget_note_with_a_failed_sampling_request()
    {
        var page = Page(text: ["some page text"], scroll: NoScroll);
        var sampling = WithSampling(throws: new McpException("client declined the sampling request"));

        var json = await Scrape(Tools(Web(), UiaCutByTheBudget(500, page), Windows()), sampling,
            source: "dom", window: "Edge", summarize: true);

        var note = Json(json).GetProperty("Note").GetString();
        note.Should().Contain("--max-tree-elements");
        note.Should().Contain("client declined the sampling request");
        note!.IndexOf("--max-tree-elements", StringComparison.Ordinal)
            .Should().BeLessThan(note.IndexOf("client declined", StringComparison.Ordinal),
                "the page's own problem is read first");
    }

    // ---- source: dom -----------------------------------------------------------------------------

    [Fact]
    public async Task Scrape_dom_with_a_window_walks_that_window_and_never_lists_the_desktop()
    {
        var page = Page(window: "A5 Probe Page", title: "A5 Probe Page", url: "http://127.0.0.1:9999/a5",
            scroll: new ScrollInfo(0, 0, true, false), text: ["Probe heading", "First paragraph."]);
        var uia = Uia(page);
        var windows = Windows();

        var json = await Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom", window: "Edge");

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Window && r.WindowTitle == "Edge" && r.UseDom),
            It.IsAny<CancellationToken>()), Times.Once);
        windows.VerifyNoOtherCalls();

        var result = Json(json);
        result.GetProperty("Source").GetString().Should().Be("dom");
        result.GetProperty("Title").GetString().Should().Be("A5 Probe Page");
        result.GetProperty("Url").GetString().Should().Be("http://127.0.0.1:9999/a5");
        result.GetProperty("Content").GetString().Should()
            .Be($"Probe heading\nFirst paragraph.\n\n{TopHint}", "the rendered page text plus its scroll hint");
        result.GetProperty("Chars").GetInt32().Should().Be($"Probe heading\nFirst paragraph.\n\n{TopHint}".Length);
        result.GetProperty("Truncated").GetBoolean().Should().BeFalse();
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Scrape_dom_caps_the_rendered_text_at_max_chars()
    {
        var page = Page(text: ["Probe heading", "First paragraph."], scroll: NoScroll);
        var whole = "Probe heading\nFirst paragraph.";

        var json = await Scrape(Tools(Web(), Uia(page), Windows()), NoSampling(),
            source: "dom", window: "Edge", max_chars: 5);

        var result = Json(json);
        result.GetProperty("Content").GetString().Should().Be(whole[..5]);
        result.GetProperty("Truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("Chars").GetInt32().Should().Be(whole.Length, "the size before the cap");
    }

    [Fact]
    public async Task Scrape_dom_without_a_window_takes_the_frontmost_browser()
    {
        var uia = Uia(Page(text: ["Probe heading"], scroll: null));
        var windows = Windows(
            Window(title: "Untitled - Notepad", process: "notepad", zOrder: 0, isBrowser: false),
            Window(title: "A5 Probe Page - Microsoft Edge", process: "msedge", zOrder: 1, isBrowser: true),
            Window(title: "Another tab - Microsoft Edge", process: "msedge", zOrder: 2, isBrowser: true));

        await Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom");

        windows.Verify(s => s.ListAsync(false, false, It.IsAny<CancellationToken>()), Times.Once,
            "a minimised window has no page to read");
        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.WindowTitle == "A5 Probe Page - Microsoft Edge" && r.UseDom),
            It.IsAny<CancellationToken>()), Times.Once,
            "the FIRST browser row in z-order is the tab the agent means, not the first window of any kind");
    }

    /// <summary>
    /// F4: "the frontmost browser" is the frontmost <b>Chromium</b> window, decided from the
    /// process name — the same set A-5's DOM walk supports. The inventory's <c>IsBrowser</c> is a
    /// wider claim (it includes Firefox), and a process name can arrive with or without its
    /// extension and in any case.
    /// </summary>
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("vivaldi")]
    [InlineData("MSEDGE")]
    [InlineData("Chrome.exe")]
    [InlineData("msedge.EXE")]
    public async Task Scrape_dom_without_a_window_takes_the_frontmost_chromium_window(string process)
    {
        var uia = Uia(Page(text: ["Probe heading"], scroll: null));
        var windows = Windows(Window(title: "Some tab", process: process, zOrder: 0, isBrowser: true));

        await Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom");

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.WindowTitle == "Some tab"), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// F4: Firefox is in the inventory's browser set but exposes no page document to UIA, so
    /// walking it produces an empty page and a note. The Chromium window behind it is the one the
    /// agent can actually be given, and taking it is better than answering nothing about the
    /// window in front.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_without_a_window_skips_a_firefox_window_in_front_of_a_chromium_one()
    {
        var uia = Uia(Page(text: ["Probe heading"], scroll: null));
        var windows = Windows(
            Window(title: "Wikipedia", process: "firefox", zOrder: 0, isBrowser: true),
            Window(title: "A5 Probe Page - Microsoft Edge", process: "msedge", zOrder: 1, isBrowser: true));

        await Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom");

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.WindowTitle == "A5 Probe Page - Microsoft Edge"),
            It.IsAny<CancellationToken>()), Times.Once,
            "source:dom reads Chromium pages; the frontmost window it can read is the one to read");
    }

    /// <summary>
    /// F4: with nothing but Firefox open, the refusal has to name Firefox. "No browser window is
    /// open" is false to the agent's face — it is looking at one — and it does not say what to do,
    /// so the agent opens another Firefox window and asks again.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_with_only_firefox_open_says_firefox_is_not_supported()
    {
        var uia = Uia();
        // The title deliberately does not contain "Firefox": the word can only reach the message
        // from the rule, not from the list of open windows.
        var windows = Windows(Window(title: "Wikipedia", process: "firefox", isBrowser: true));

        Func<Task> act = () => Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("firefox", "the agent is told which browser it is looking at");
        message.Should().Contain("Chromium", "and which family source:dom can read");
        message.Should().Contain("source:http", "and how to read the page anyway");
        message.Should().NotContainEquivalentOf("none is open",
            "a browser IS open; telling the agent otherwise makes it open another one");
        // The walk is never attempted: a Firefox window has no page document to find.
        uia.VerifyNoOtherCalls();
    }

    /// <summary>
    /// F4: the pick reads the process name, not the inventory's flag. A window flagged
    /// <c>IsBrowser</c> that is not a Chromium process has no page to walk, and preferring it over
    /// the Edge window behind it loses the page the agent asked for.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_does_not_pick_a_non_chromium_window_flagged_as_a_browser()
    {
        var uia = Uia(Page(text: ["Probe heading"], scroll: null));
        var windows = Windows(
            Window(title: "Untitled - Notepad", process: "notepad", zOrder: 0, isBrowser: true),
            Window(title: "A5 Probe Page - Microsoft Edge", process: "msedge", zOrder: 1, isBrowser: true));

        await Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom");

        uia.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.WindowTitle == "A5 Probe Page - Microsoft Edge"),
            It.IsAny<CancellationToken>()), Times.Once,
            "the process name is what says a page can be read out of the window");
    }

    [Fact]
    public async Task Scrape_dom_with_no_browser_window_open_is_refused_with_the_titles()
    {
        var uia = Uia();
        var windows = Windows(
            Window(title: "Untitled - Notepad", process: "notepad", isBrowser: false),
            Window(title: "Windows PowerShell", process: "powershell", isBrowser: false));

        Func<Task> act = () => Scrape(Tools(Web(), uia, windows), NoSampling(), source: "dom");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage("*dom*", "the refusal names the source that cannot be served");
        thrown.WithMessage("*Untitled - Notepad*", "it lists what IS open so the agent can pick");
        thrown.WithMessage("*Windows PowerShell*", "every open title, not just the first");
        thrown.WithMessage("*source:http*",
            "and the alternative that needs no browser at all is in the message, not only in the docs");
        uia.VerifyNoOtherCalls();
    }

    /// <summary>
    /// C-5: the same refusal with nothing at all on the desktop. "Open windows: " followed by
    /// nothing reads as a truncated message; <c>(none)</c> says the inventory really was empty.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_with_no_windows_at_all_says_none_rather_than_listing_nothing()
    {
        Func<Task> act = () => Scrape(Tools(Web(), Uia(), Windows()), NoSampling(), source: "dom");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage("*(none)*", "an empty list is stated, not left as an empty gap in the sentence");
        thrown.WithMessage("*source:http*", "and the alternative still comes with it");
    }

    [Fact]
    public async Task Scrape_dom_of_a_window_that_yields_no_page_is_refused_by_name()
    {
        var uia = Uia();   // Pages: an empty array - the window is not a Chromium browser

        Func<Task> act = () => Scrape(Tools(Web(), uia, Windows()), NoSampling(), source: "dom", window: "Notepad");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage("*Notepad*", "the caller is told which window it was");
        thrown.WithMessage("*Chromium*", "and what the rule is - only Chromium windows expose a page");
        thrown.WithMessage("*source:http*",
            "the message carries the alternative that still works: a refusal that names no way "
            + "forward makes the model guess");
        thrown.WithMessage("*url*", "and what source:http needs to be given");
    }

    [Fact]
    public async Task Scrape_dom_of_a_snapshot_with_no_pages_block_at_all_is_refused_by_name()
    {
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Result(pages: null));   // null, not empty: the walk reported no Pages section

        Func<Task> act = () => Scrape(Tools(Web(), uia, Windows()), NoSampling(), source: "dom", window: "Notepad");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage("*Notepad*");
        thrown.WithMessage("*Chromium*");
    }

    /// <summary>
    /// F3: a page entry with a <c>Note</c> and no <c>DocumentId</c> is A-5 saying it never found the
    /// web page — the window is still loading, or it is Firefox, or it is not a web view at all.
    /// What it walked instead is the browser's own chrome: the tab strip, the address bar, the
    /// bookmarks. Returning that as <c>Content</c> with an explanatory note hands the model a
    /// plausible-looking page that is not the page, and the note is one field away from where the
    /// model is reading. A refusal cannot be misread, and it carries the same explanation.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_of_a_page_with_a_note_and_no_document_is_refused_with_the_note()
    {
        var uia = Uia(NoDocument("no page document found under this window: it may still be loading"));

        Func<Task> act = () => Scrape(Tools(Web(), uia, Windows()), NoSampling(), source: "dom", window: "Edge");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "the browser's chrome is not the page, and 'some window text' with a note beside it "
            + "reads exactly like a page that happens to be short");
        thrown.WithMessage("*it may still be loading*", "the page's own reason is carried, not replaced");
        thrown.WithMessage("*Edge*", "the caller is told which window it was");
        thrown.WithMessage("*Chromium*", "and what the rule is");
        thrown.WithMessage("*source:http*", "and the way forward that needs no browser at all");
    }

    /// <summary>
    /// F3: one window can produce several page entries (a browser walked with several web views).
    /// The page is the first one that actually has a document — a note-only entry ahead of it must
    /// not shadow a page that was read perfectly well.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_takes_the_first_page_that_has_a_document()
    {
        var real = Page(documentId: "el_7", title: "A5 Probe Page", url: "http://127.0.0.1:9999/a5",
            scroll: NoScroll, text: ["Probe heading"]);

        var json = await Scrape(Tools(Web(), Uia(NoDocument(), real), Windows()), NoSampling(),
            source: "dom", window: "Edge");

        var result = Json(json);
        result.GetProperty("Content").GetString().Should().Be("Probe heading");
        result.GetProperty("Title").GetString().Should().Be("A5 Probe Page");
        result.GetProperty("Url").GetString().Should().Be("http://127.0.0.1:9999/a5");
        result.GetProperty("Content").GetString().Should().NotContain("some window text",
            "the entry with no document is skipped, not preferred for being first");
    }

    /// <summary>
    /// F2: <c>Pages</c> empty because the ELEMENT BUDGET ended the walk before it reached a page is
    /// a different fact from "this window is not a browser", and it has a different remedy. Telling
    /// an agent that its Edge window is not a browser sends it to open another one.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_when_the_element_budget_ended_the_walk_before_a_page_says_so()
    {
        var uia = UiaCutByTheBudget(500);   // truncated, and no page was reached

        Func<Task> act = () => Scrape(Tools(Web(), uia, Windows()), NoSampling(), source: "dom", window: "Edge");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("element", "the walk ran out of element budget");
        message.Should().Contain("500", "and the caller is told what the budget was");
        message.Should().NotContainEquivalentOf("is not a browser",
            "the window is a browser; the walk simply never got as far as its page");
    }

    [Fact]
    public async Task Scrape_dom_summarizes_the_rendered_page_when_asked()
    {
        var page = Page(text: ["Probe heading"], scroll: NoScroll);
        var sampling = WithSampling();

        var json = await Scrape(Tools(Web(), Uia(page), Windows()), sampling,
            source: "dom", window: "Edge", summarize: true);

        sampling.Requests.Should().ContainSingle().Which
            .Messages[0].Content[0].Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("Probe heading", "the DOM text is what gets summarised");
        Json(json).GetProperty("Summarized").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Scrape_dom_summarizes_only_what_survived_the_cap()
    {
        var page = Page(text: ["Probe heading", "First paragraph."], scroll: null);
        var sampling = WithSampling();

        await Scrape(Tools(Web(), Uia(page), Windows()), sampling,
            source: "dom", window: "Edge", summarize: true, max_chars: 5);

        sampling.Requests.Should().ContainSingle().Which
            .Messages[0].Content[0].Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("Probe",
                "max_chars truncates BEFORE sampling - the client is not billed for text the "
                + "caller already said it did not want");
    }

    // ---- F2: a walk the element budget cut short is reported ------------------------------------

    /// <summary>
    /// F2: the walk stopped at the budget, so the page text stops somewhere arbitrary — mid-page,
    /// with no marker. <c>Truncated</c> is the field that already means "there is more of this than
    /// you were given", and the note has to name the number and the knob, because the caller of
    /// <c>scrape</c> has no <c>max_elements</c> parameter to raise and would otherwise have no idea
    /// what to change.
    /// </summary>
    [Fact]
    public async Task Scrape_dom_of_a_walk_the_element_budget_cut_short_says_so()
    {
        var page = Page(text: ["Probe heading", "First paragraph."], scroll: NoScroll);
        var whole = "Probe heading\nFirst paragraph.";

        var json = await Scrape(Tools(Web(), UiaCutByTheBudget(500, page), Windows()), NoSampling(),
            source: "dom", window: "Edge");

        var result = Json(json);
        result.GetProperty("Truncated").GetBoolean().Should().BeTrue(
            "the text stops before the page does, which is exactly what Truncated says");
        var note = result.GetProperty("Note").GetString();
        note.Should().Contain("500", "the budget that was hit");
        note.Should().Contain("--max-tree-elements", "and the only knob that raises it for scrape");
        result.GetProperty("Chars").GetInt32().Should().Be(whole.Length,
            "Chars is still the size of the text that was rendered - the budget cut the WALK, not the text");
        result.GetProperty("Content").GetString().Should().Be(whole, "nothing was cut a second time");
    }

    [Fact]
    public async Task Scrape_dom_of_a_complete_walk_carries_no_budget_note()
    {
        var page = Page(text: ["Probe heading"], scroll: NoScroll);

        var json = await Scrape(Tools(Web(), Uia(page), Windows()), NoSampling(), source: "dom", window: "Edge");

        var result = Json(json);
        result.GetProperty("Truncated").GetBoolean().Should().BeFalse();
        result.GetProperty("Note").ValueKind.Should().Be(JsonValueKind.Null,
            "nothing was cut, so a note about the element budget would be noise the model has to reason about");
    }

    // ---- F13: the model is told when it is holding only the beginning ---------------------------

    /// <summary>
    /// F13: a summary of a capped page is a summary of the FRONT of a page. Without the truncated
    /// prompt the model answers "the page does not mention it" about text it never saw — an answer
    /// indistinguishable from a real one, produced by a tool the agent trusted.
    /// <para>
    /// What this row and the two below own is the FLAG the tool passes; the wording it selects is
    /// pinned in <c>ScrapeSummaryTests</c> (the two prompts differ, so passing the wrong flag is
    /// caught here). The hedge itself is asserted once, below, so the row still says what it is
    /// about without reading the other file.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_of_a_page_the_cap_cut_sends_the_truncated_prompt()
    {
        var web = Web(new ScrapeResult("http", "https://example.test/", "Example", 90_000, true, "page text"));
        var sampling = WithSampling();

        await Scrape(Tools(web), sampling, url: "https://example.test/", query: "what is the price", summarize: true);

        var prompt = sampling.Requests.Should().ContainSingle().Which.SystemPrompt;
        prompt.Should().Be(
            WindowsMcp.Services.ScrapeSummary.SystemPrompt("what is the price", true),
            "Truncated:true is the tool's own signal that the model is holding a fragment");
        prompt.Should().Contain("only the beginning of a longer page",
            "the model has to be told in words, not by a flag it never sees");
        prompt.Should().Contain("what is the price", "and the question still travels with it");
    }

    [Fact]
    public async Task Scrape_summarize_of_a_walk_the_element_budget_cut_short_sends_the_truncated_prompt()
    {
        var page = Page(text: ["Probe heading"], scroll: NoScroll);
        var sampling = WithSampling();

        await Scrape(Tools(Web(), UiaCutByTheBudget(500, page), Windows()), sampling,
            source: "dom", window: "Edge", summarize: true);

        sampling.Requests.Should().ContainSingle().Which.SystemPrompt.Should().Be(
            WindowsMcp.Services.ScrapeSummary.SystemPrompt(null, true),
            "a walk cut by the budget hands the model a fragment just as a max_chars cut does");
    }

    [Fact]
    public async Task Scrape_summarize_of_a_whole_page_sends_the_plain_prompt()
    {
        var sampling = WithSampling();

        await Scrape(Tools(Web()), sampling, url: "https://example.test/", query: "what is the price", summarize: true);

        sampling.Requests.Should().ContainSingle().Which.SystemPrompt.Should().Be(
            WindowsMcp.Services.ScrapeSummary.SystemPrompt("what is the price", false),
            "nothing was cut, so nothing is hedged");
    }

    // ---- F10: the summary is bounded, and never costs the page -----------------------------------

    /// <summary>
    /// F10: the client's model gets two minutes. The number is a decision — long enough for a real
    /// model call over a slow link, short enough that a client which simply never replies does not
    /// hold the tool call (and the caller's own request timeout) open indefinitely.
    /// </summary>
    [Fact]
    public void The_sampling_budget_is_two_minutes()
        => WebTools.SamplingTimeout.Should().Be(TimeSpan.FromSeconds(120));

    /// <summary>
    /// F10: <c>SampleAsync</c> is a request to the CLIENT, over a channel the server does not
    /// control; a client that never answers hangs the call forever. The tool therefore hands the
    /// seam a token of its own — with <c>CancellationToken.None</c> from the caller, a token that
    /// can be cancelled at all can only be the tool's deadline.
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_gives_the_client_a_deadline_of_the_tools_own()
    {
        var sampling = WithSampling();

        await Scrape(Tools(Web()), sampling, url: "https://example.test/", summarize: true);

        var token = sampling.Tokens.Should().ContainSingle().Subject;
        token.CanBeCanceled.Should().BeTrue(
            "the caller passed CancellationToken.None, which can never be cancelled - a token that "
            + "can be is the tool's own clock");
        token.Should().NotBe(CancellationToken.None);
    }

    /// <summary>
    /// F10: when that deadline is what ended the sampling call, the page still comes back. The
    /// cancellation carries the TOOL's token, not the caller's — which is how the tool tells its own
    /// deadline apart from the caller giving up (the row below, which still throws).
    /// </summary>
    [Fact]
    public async Task Scrape_summarize_that_the_client_never_answers_returns_the_page_and_says_so()
    {
        var sampling = new FakeSampling { Supported = true, NeverAnswers = true };

        var json = await Scrape(Tools(Web()), sampling, url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text",
            "a client that does not answer must not cost the caller the page it did fetch");
        var note = result.GetProperty("Note").GetString();
        note.Should().ContainEquivalentOf("did not answer");
        note.Should().Contain(((int)WebTools.SamplingTimeout.TotalSeconds).ToString(),
            "the caller is told how long it waited, so it can tell a slow client from a broken one");
    }

    /// <summary>
    /// F10: the client can fail in more ways than <c>McpException</c> — an SDK that does not support
    /// sampling throws <c>InvalidOperationException</c>, a dropped connection throws
    /// <c>IOException</c>, a malformed reply throws <c>JsonException</c>. Every one of them is the
    /// summary failing, and none of them is a reason to lose the page that was already fetched.
    /// </summary>
    [Theory]
    [InlineData("unsupported", "client does not support sampling")]
    [InlineData("io", "the connection to the client was closed")]
    [InlineData("json", "unexpected token in the sampling reply")]
    public async Task Scrape_summarize_that_fails_inside_the_client_returns_the_page_and_the_reason(
        string kind, string message)
    {
        Exception failure = kind switch
        {
            "unsupported" => new InvalidOperationException(message),
            "io" => new IOException(message),
            _ => new JsonException(message),
        };

        var json = await Scrape(Tools(Web()), WithSampling(throws: failure),
            url: "https://example.test/", summarize: true);

        var result = Json(json);
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
        result.GetProperty("Content").GetString().Should().Be("page text", "the page survives the summary");
        result.GetProperty("Note").GetString().Should().Contain(message,
            "the caller has to be able to tell a client that cannot sample from a page with no text");
    }

    // ---- http_request (unchanged by C-5) ---------------------------------------------------------

    [Fact]
    public async Task WebTools_HttpRequest_serializes_response()
    {
        var mockWeb = new Mock<IWebService>();
        var dto = new HttpResponseDto(
            Status: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "text/html" },
            Body: "<h1>Test</h1>");
        mockWeb
            .Setup(s => s.RequestAsync("https://example.com", "GET", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var json = await Tools(mockWeb).HttpRequest("https://example.com", "GET");

        json.Should().Contain("200");
        json.Should().Contain("Test");
        mockWeb.VerifyAll();
    }

    /// <summary>
    /// <c>headers_json</c> is documented as "JSON object of header name-&gt;value", and parsing it
    /// is the only work this tool does. A tool that dropped the headers would send an
    /// unauthenticated request and report the 401 as the server's answer, with nothing in the
    /// result to say the Authorization header never left.
    /// </summary>
    [Fact]
    public async Task HttpRequest_parses_headers_json_into_the_headers_it_forwards()
    {
        var mockWeb = new Mock<IWebService>();
        IDictionary<string, string>? forwarded = null;
        mockWeb.Setup(s => s.RequestAsync(
                   It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(),
                   It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .Callback((string _, string _, IDictionary<string, string>? h, string? _, CancellationToken _) => forwarded = h)
               .ReturnsAsync(new HttpResponseDto(200, new Dictionary<string, string>(), "ok"));

        await Tools(mockWeb).HttpRequest(
            "https://example.com", "POST",
            headers_json: "{\"Authorization\":\"Bearer abc\",\"X-Custom\":\"one two\"}",
            body: "{\"a\":1}");

        forwarded.Should().NotBeNull().And.HaveCount(2);
        forwarded!["Authorization"].Should().Be("Bearer abc");
        forwarded["X-Custom"].Should().Be("one two");
        mockWeb.Verify(s => s.RequestAsync(
            "https://example.com", "POST", It.IsAny<IDictionary<string, string>>(), "{\"a\":1}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// No <c>headers_json</c> means no headers — null, not an empty map. The service takes
    /// <c>IDictionary?</c> and an empty map is indistinguishable from "the caller sent headers
    /// that all got dropped".
    /// </summary>
    [Fact]
    public async Task HttpRequest_forwards_null_headers_when_none_were_given()
    {
        var mockWeb = new Mock<IWebService>();
        mockWeb.Setup(s => s.RequestAsync(
                   It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(),
                   It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new HttpResponseDto(200, new Dictionary<string, string>(), "ok"));

        await Tools(mockWeb).HttpRequest("https://example.com");

        mockWeb.Verify(s => s.RequestAsync("https://example.com", "GET", null, null, It.IsAny<CancellationToken>()),
            Times.Once, "GET is the documented default method and no headers means none");
    }

    /// <summary>
    /// C-5 GREEN: a <c>headers_json</c> the tool cannot read is the caller's typo, and the caller
    /// is the only one who can fix it — so it has to be told which argument is wrong.
    /// <see cref="JsonException"/> is not on <c>ToolErrors.IsCallerFacing</c>, so a raw one reaches
    /// the client as the SDK's "An error occurred invoking 'http_request'." and says nothing at
    /// all. The refusal also has to come BEFORE the request: sending it with the headers silently
    /// dropped turns the caller's typo into a 401 that looks like the server's answer.
    /// </summary>
    [Theory]
    [InlineData("{not json")]            // not JSON at all
    [InlineData("[1,2]")]                // valid JSON, wrong shape: an array, not an object
    [InlineData("{\"Accept\":1}")]       // a JSON object, but of something that is not a header value
    public async Task HttpRequest_refuses_a_headers_json_it_cannot_read_and_sends_no_request(string headers_json)
    {
        var mockWeb = new Mock<IWebService>(MockBehavior.Strict);

        Func<Task> act = () => Tools(mockWeb).HttpRequest("https://example.com", "GET", headers_json);

        var thrown = await act.Should().ThrowAsync<ArgumentException>(
            "a JsonException would be masked by the SDK, and the mask hides the one thing the caller can act on");
        thrown.WithMessage("*headers_json*", "the message names the argument to fix");
        thrown.And.ParamName.Should().Be("headers_json");
        mockWeb.Verify(s => s.RequestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "nothing goes on the wire once the headers the caller asked for cannot be read");
    }

    /// <summary>
    /// C-5 GREEN: <c>{}</c> parses — it is a JSON object of header name to value with no entries
    /// — so it is not a refusal, and what reaches the service is an EMPTY map, not null. The two
    /// are different answers to "did the caller send headers?", and the parse must not turn the one
    /// the caller sent into the other on the way through.
    /// </summary>
    [Fact]
    public async Task HttpRequest_forwards_an_empty_map_when_headers_json_is_an_empty_object()
    {
        var mockWeb = new Mock<IWebService>();
        IDictionary<string, string>? forwarded = null;
        mockWeb.Setup(s => s.RequestAsync(
                   It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(),
                   It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .Callback((string _, string _, IDictionary<string, string>? h, string? _, CancellationToken _) => forwarded = h)
               .ReturnsAsync(new HttpResponseDto(200, new Dictionary<string, string>(), "ok"));

        await Tools(mockWeb).HttpRequest("https://example.com", "GET", headers_json: "{}");

        mockWeb.Verify(s => s.RequestAsync(
                "https://example.com", "GET", It.IsAny<IDictionary<string, string>>(), null,
                It.IsAny<CancellationToken>()),
            Times.Once, "an empty object is readable: the request still goes out");
        forwarded.Should().NotBeNull(
            "an empty map says 'the caller sent no header'; null says 'the caller sent no headers_json'")
            .And.BeEmpty();
    }

    // ---- D-11 R17: a dom scrape expires the caller's snapshot ids -----------------------------

    [Fact]
    public void Scrape_description_says_a_dom_read_invalidates_the_previous_snapshots_ids()
    {
        // source:"dom" walks the browser through SnapshotAsync, so every id the caller's last
        // snapshot issued expires. An agent that scrapes between a snapshot and a click has to
        // be told that BEFORE it loses its ids, not by the expired-id error afterwards.
        var description = typeof(WebTools).GetMethod(nameof(WebTools.Scrape))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        description.Should().ContainEquivalentOf("snapshot", "the ids at stake are the snapshot's");
        description.Should().MatchRegex("(?i)(invalidat|expire|replace)", "and what happens to them is said outright");
    }
}
#pragma warning restore MCP9005
