using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;

namespace WindowsMcp.Tools;

// MCP9005: see ISamplingClient.cs — the summary path is client-side sampling by decision (C-5).
#pragma warning disable MCP9005

[McpServerToolType]
public sealed class WebTools
{
    /// <summary>C-5: the largest <c>max_chars</c>; the default is 100 000.</summary>
    internal const int MaxChars = 1_000_000;

    /// <summary>
    /// C-5 (review F10): how long <c>summarize:true</c> waits for the client's model. A sampling
    /// request is answered by the client, over a channel this server does not control; a client
    /// whose handler prompts a user who walks away would otherwise hang the call forever.
    /// </summary>
    internal static readonly TimeSpan SamplingTimeout = TimeSpan.FromSeconds(120);

    private const string ChromiumBrowsers = "Edge, Chrome, Brave, Opera, Vivaldi";

    private const string NoSamplingNote =
        "summarize:true was ignored: this client did not declare the sampling capability, so the content is returned as is";

    private const string StatelessNote =
        "summarize:true was ignored: this server is running over its stateless HTTP transport, which keeps no " +
        "session to carry a sampling request to the client; run the server over stdio for summaries. The " +
        "content is returned as is";

    private readonly IWebService _web;
    private readonly IUIAutomationService _uia;
    private readonly IWindowService _windows;
    private readonly TransportOptions _transport;

    public WebTools(IWebService web, IUIAutomationService uia, IWindowService windows, TransportOptions? transport = null)
    {
        _web = web;
        _uia = uia;
        _windows = windows;
        _transport = transport ?? TransportOptions.Stdio;
    }

    [McpServerTool(Title = "Scrape web page", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true), Description(
        "Read a web page as text. source:'http' (default) fetches url (http/https only; private IPs " +
        "rejected) and converts the HTML to markdown; source:'dom' reads the page already open in a " +
        "Chromium browser (Edge, Chrome, Brave, Opera, Vivaldi; not Firefox) — the window given by " +
        "'window', else the frontmost such window — with a scroll hint saying whether there is more " +
        "above or below. A dom read walks the browser as a snapshot does, so it replaces the previous " +
        "snapshot's element ids (el_N): take a new snapshot before acting on one. Returns JSON {Source, Url, Title, Chars, Truncated, Content, Summarized, " +
        "Model, Note}: Chars is the size before max_chars cut it, and Truncated says the text was cut " +
        "by max_chars or by the element budget of a dom walk (Note then names --max-tree-elements). " +
        "summarize:true asks YOUR client's model to condense the page (and to answer 'query' from it, " +
        "if given), waiting up to 120s; clients without the sampling capability, and this server's " +
        "stateless HTTP transport, return the text unchanged with Note saying so.")]
    public Task<string> Scrape(
        McpServer server,
        [Description("Public URL to fetch (http/https; private IPs rejected). Required for source:'http', refused for source:'dom'")] string? url = null,
        [Description("A question to answer from the page; only valid with summarize:true")] string? query = null,
        [Description("http (fetch the url) | dom (read the browser's open page)")] string source = "http",
        [Description("Ask the client's model to summarize the page instead of returning the raw text")] bool summarize = false,
        [Description("Cap the text at this many characters (1..1000000)")] int max_chars = 100000,
        [Description("Title of the browser window to read; only valid with source:'dom'")] string? window = null,
        CancellationToken ct = default)
        => ScrapeAsync(url, query, source, summarize, max_chars, window, new McpServerSampling(server), ct);

    /// <summary>
    /// C-5: the tool's real body, with the sampling seam injected so the unit tests can fake it.
    /// Every refusal runs before anything is fetched or walked.
    /// </summary>
    internal async Task<string> ScrapeAsync(
        string? url, string? query, string source, bool summarize, int max_chars, string? window,
        ISamplingClient sampling, CancellationToken ct)
    {
        var kind = source.ToLowerInvariant() switch
        {
            "http" => "http",
            "dom" => "dom",
            _ => throw new ArgumentException($"Unknown source '{source}'; expected http|dom.", nameof(source)),
        };
        if (max_chars < 1 || max_chars > MaxChars)
            throw new ArgumentException($"max_chars must be 1-{MaxChars} (0 is not 'all'), got {max_chars}.", nameof(max_chars));
        if (kind == "http")
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException(
                    "url is required with source:http. To read the page already open in the browser instead, pass source:dom.",
                    nameof(url));
            if (!string.IsNullOrWhiteSpace(window))
                throw new ArgumentException("window is only used with source:dom; source:http fetches url.", nameof(window));
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException(
                "url is only used with source:http; source:dom reads the page that is already open " +
                "(name its window with window:<title>, or leave it to the frontmost browser window).",
                nameof(url));
        }
        if (!string.IsNullOrWhiteSpace(query) && !summarize)
            throw new ArgumentException("query needs summarize:true: the summary is what answers it.", nameof(query));

        var result = kind == "http"
            ? await _web.ScrapeAsync(url!, max_chars, ct)
            : await ScrapeDomAsync(window, max_chars, ct);

        if (summarize)
        {
            // A stateless transport has no session for the client's reply to come back on, so the
            // client is never asked — whatever capability it declared at initialize is not known
            // to this per-request server anyway.
            result = _transport.Stateless
                ? result with { Note = Join(result.Note, StatelessNote) }
                : await SummarizeAsync(result, query, sampling, ct);
        }

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// The live page: the named window, else the frontmost Chromium window of the A-1 inventory
    /// (review F4: Firefox is a browser but exposes no page document), walked from its page
    /// document (A-5) and rendered with the scroll hint. The page is the first entry that has a
    /// document (review F3); a walk the element budget cut short says so (review F2).
    /// </summary>
    private async Task<ScrapeResult> ScrapeDomAsync(string? window, int maxChars, CancellationToken ct)
    {
        string title;
        if (!string.IsNullOrWhiteSpace(window))
        {
            title = window;
        }
        else
        {
            var open = (await _windows.ListAsync(includeMinimized: false, includeHidden: false, ct))
                .OrderBy(w => w.ZOrder)
                .ToArray();
            var chromium = open.FirstOrDefault(w => WindowFilter.IsChromium(w.ProcessName));
            if (chromium is null)
            {
                var other = open.FirstOrDefault(w => w.IsBrowser);
                throw new InvalidOperationException(other is null
                    ? $"source:dom needs an open Chromium browser window ({ChromiumBrowsers}) and none is open. " +
                      $"Open windows: {Titles(open)}. Open the page in one of them, or fetch it with source:http and a url."
                    : $"source:dom cannot read '{other.Title}': {other.ProcessName} is not a Chromium browser, and only " +
                      $"Chromium browsers ({ChromiumBrowsers}) expose their page to UI Automation. Open the page in one " +
                      "of them, or fetch it with source:http and a url.");
            }
            title = chromium.Title;
        }

        var snapshot = await _uia.SnapshotAsync(
            new SnapshotRequest(SnapshotScope.Window, title, IncludeTree: false, MaxElements: 0, UseDom: true), ct);
        var pages = snapshot.Pages ?? [];
        var page = pages.FirstOrDefault(p => p.DocumentId is not null);
        if (page is null)
        {
            var noted = pages.FirstOrDefault(p => p.Note is not null);
            if (noted is not null)
                throw new InvalidOperationException(
                    $"source:dom: '{title}': {noted.Note}. Only Chromium browsers ({ChromiumBrowsers}) expose their page " +
                    "to UI Automation, and only once it has loaded: try again in a moment, name another window with " +
                    "window:<title>, or fetch the page with source:http and a url.");
            if (snapshot.Truncated)
                throw new InvalidOperationException(
                    $"source:dom: the walk of '{title}' hit the element budget ({snapshot.ElementLimit} elements) before " +
                    "it reached a page: another window matching the title was walked first. Name the browser window " +
                    "exactly with window:<title>, raise --max-tree-elements, or fetch the page with source:http and a url.");
            throw new InvalidOperationException(
                $"source:dom: '{title}' is not a browser window. Only Chromium browsers ({ChromiumBrowsers}) expose " +
                "their page; name one with window:<title>, or fetch the page with source:http and a url.");
        }

        var rendered = DomPage.Render(page);
        var content = TextCap.Cut(rendered, maxChars, out var cut);
        var note = snapshot.Truncated
            ? $"the page walk stopped at the element budget ({snapshot.ElementLimit} elements), so the text is the " +
              "first part of the page only; raise --max-tree-elements (or WINDOWSMCP_MAX_TREE_ELEMENTS) to read more"
            : null;
        return new ScrapeResult("dom", page.Url, page.Title, rendered.Length, cut || snapshot.Truncated, content, Note: note);
    }

    /// <summary>
    /// The summary through the client's model, bounded by <see cref="SamplingTimeout"/>. Every
    /// way it cannot happen returns the text with a <c>Note</c> saying why — the page is never
    /// lost because the summary failed. Only the caller's cancellation propagates.
    /// </summary>
    private static async Task<ScrapeResult> SummarizeAsync(
        ScrapeResult result, string? query, ISamplingClient sampling, CancellationToken ct)
    {
        if (!sampling.Supported)
            return result with { Note = Join(result.Note, NoSamplingNote) };
        if (string.IsNullOrWhiteSpace(result.Content))
            return result with { Note = Join(result.Note, "nothing to summarise: the page has no text") };

        CreateMessageResult reply;
        using var clock = new CancellationTokenSource(SamplingTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, clock.Token);
        try
        {
            reply = await sampling.SampleAsync(ScrapeSummary.Request(result.Content, query, result.Truncated), linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return result with
            {
                Note = Join(result.Note,
                    $"summarize:true failed: the client did not answer the sampling request within " +
                    $"{(int)SamplingTimeout.TotalSeconds}s; the content is returned as is"),
            };
        }
        catch (Exception ex)
        {
            return result with
            {
                Note = Join(result.Note,
                    $"summarize:true failed: the client did not complete the sampling request ({ex.Message}); the content is returned as is"),
            };
        }

        var text = string.Join('\n', (reply.Content ?? []).OfType<TextContentBlock>().Select(b => b.Text));
        if (string.IsNullOrWhiteSpace(text))
            return result with
            {
                Note = Join(result.Note, "summarize:true failed: the client's model returned no text; the content is returned as is"),
            };

        return result with { Content = text, Summarized = true, Model = reply.Model };
    }

    private static string Join(string? existing, string note)
        => existing is null ? note : FileSystemService.TwoSentences(existing, note);

    private static string Titles(IEnumerable<WindowInfo> windows)
    {
        var titles = windows.Select(w => $"'{w.Title}'").ToArray();
        return titles.Length == 0 ? "(none)" : string.Join(", ", titles);
    }

    [McpServerTool(Title = "HTTP request", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Make an HTTP request to a URL (http/https only; private IPs rejected).")]
    public async Task<string> HttpRequest(
        string url,
        [Description("GET|POST|PUT|DELETE|PATCH")] string method = "GET",
        [Description("JSON object of header name->value, e.g. {\"Authorization\":\"Bearer ...\"}")] string? headers_json = null,
        [Description("Request body for POST/PUT/PATCH")] string? body = null)
    {
        IDictionary<string, string>? headers = null;
        if (headers_json != null)
        {
            try
            {
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headers_json);
            }
            catch (JsonException ex)
            {
                // Caller-facing: a mistyped JSON object is the caller's to fix, not "an error occurred".
                throw new ArgumentException(
                    $"headers_json must be a JSON object of header name to value, e.g. {{\"Accept\":\"text/html\"}}: {ex.Message}",
                    nameof(headers_json));
            }
        }
        var result = await _web.RequestAsync(url, method, headers, body);
        return JsonSerializer.Serialize(result);
    }
}
#pragma warning restore MCP9005
