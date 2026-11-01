using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed partial class WebService : IWebService
{
    private const int MaxResponseBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRedirects = 5;

    // One HttpClient per service, and the service is a process singleton — the standard
    // guidance for the .NET HttpClient lifecycle. An instance field rather than a static so a
    // test can give it a short timeout (C-5) without touching every other test's client.
    private readonly HttpClient _client;
    private readonly bool _allowPrivateIps;
    private readonly ILogger? _log;

    /// <summary>
    /// C-5 (review F9): the deepest element nesting below <c>&lt;body&gt;</c> the HTML→Markdown
    /// conversion is asked to walk. ReverseMarkdown recurses once per level and overflows the
    /// stack near 900 — which no <c>catch</c> can stop and which takes the whole server with it —
    /// so a deeper document is refused with an answer the caller can read. Measured iteratively
    /// on the parsed tree before the converter sees it.
    /// </summary>
    internal const int MaxNestingDepth = 300;

    /// <summary>Production constructor: SSRF protection is active (allowPrivateIps = false).</summary>
    public WebService(ILogger<WebService>? log = null)
        : this(allowPrivateIps: false, log) { }

    /// <summary>
    /// Test-accessible constructor. Set allowPrivateIps: true when using LocalHttpServerFixture
    /// (which binds to 127.0.0.1, otherwise blocked by SSRF protection).
    /// </summary>
    public WebService(bool allowPrivateIps, ILogger<WebService>? log = null)
        : this(allowPrivateIps, httpTimeout: null, log) { }

    /// <summary>
    /// C-5: the timeout the client applies to every request (its 100-second default when null),
    /// so a test can prove that a host that never answers is reported as a <see cref="TimeoutException"/>.
    /// </summary>
    internal WebService(bool allowPrivateIps, TimeSpan? httpTimeout, ILogger<WebService>? log = null)
    {
        _allowPrivateIps = allowPrivateIps;
        _log = log;
        _client = CreateClient(allowPrivateIps);
        if (httpTimeout is { } timeout) _client.Timeout = timeout;
    }

    private static HttpClient CreateClient(bool allowPrivateIps)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = allowPrivateIps ? null : BlockPrivateConnectAsync,
        };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    /// <inheritdoc />
    public async Task<ScrapeResult> ScrapeAsync(string url, int maxChars = 100000, CancellationToken ct = default)
    {
        if (maxChars < 1)
            throw new ArgumentException($"maxChars must be positive, got {maxChars}.", nameof(maxChars));
        ct.ThrowIfCancellationRequested();
        string html;
        string finalUrl;
        try
        {
            using var response = await SendWithRedirectValidationAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            response.EnsureSuccessStatusCode();
            html = await ReadBodyCappedAsync(response, ct);
            // After redirects: the request the content actually answered — without any
            // credentials the caller put in the URL (review F12), which the model would quote back.
            finalUrl = WithoutUserInfo(response.RequestMessage?.RequestUri) ?? WithoutUserInfo(new Uri(url)) ?? url;
        }
        catch (HttpRequestException ex)
        {
            // Caller-facing (ToolErrors): a 404 or a refused connection is an answer, not a fault.
            throw new InvalidOperationException($"{url} could not be fetched: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var seconds = _client.Timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            throw new TimeoutException($"{url} did not respond within {seconds}s.", ex);
        }

        var document = new HtmlParser().ParseDocument(html);
        var depth = NestingDepth(document);
        if (depth > MaxNestingDepth)
            throw new InvalidOperationException(
                $"{url} nests its elements {depth} levels deep, past the {MaxNestingDepth}-level limit the " +
                "HTML-to-Markdown conversion can walk. Read the raw HTML with http_request instead.");

        var markdown = new ReverseMarkdown.Converter().Convert(html);
        var content = TextCap.Cut(markdown, maxChars, out var truncated);
        return new ScrapeResult("http", finalUrl, TitleOf(document), markdown.Length, truncated, content);
    }

    /// <summary>
    /// The document's own title — the first HTML <c>&lt;title&gt;</c> element, as a browser
    /// resolves it (an SVG tooltip, a commented-out tag or a string inside a script is not one) —
    /// entities decoded and whitespace collapsed; null when the page has none or it is blank.
    /// </summary>
    internal static string? HtmlTitle(string html) => TitleOf(new HtmlParser().ParseDocument(html));

    private static string? TitleOf(IDocument document)
    {
        var title = Whitespace().Replace(document.Title ?? "", " ").Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>
    /// How deep the elements below <c>&lt;body&gt;</c> nest (its own children are level 1),
    /// walked with an explicit stack so the measurement itself cannot overflow.
    /// </summary>
    internal static int NestingDepth(IDocument document)
    {
        var body = document.Body;
        if (body is null) return 0;
        int deepest = 0;
        var stack = new Stack<(IElement Element, int Depth)>();
        stack.Push((body, 0));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > deepest) deepest = depth;
            foreach (var child in element.Children)
                stack.Push((child, depth + 1));
        }
        return deepest;
    }

    /// <summary>
    /// The URL as it was fetched (<see cref="Uri.AbsoluteUri"/>, every escape intact — not the
    /// display form <c>ToString()</c> gives, which unescapes <c>%20</c>), minus any user info.
    /// </summary>
    private static string? WithoutUserInfo(Uri? uri)
    {
        if (uri is null) return null;
        if (string.IsNullOrEmpty(uri.UserInfo)) return uri.AbsoluteUri;
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public async Task<HttpResponseDto> RequestAsync(
        string url,
        string method,
        IDictionary<string, string>? headers,
        string? body,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateHttpMethod(method);

        using var response = await SendWithRedirectValidationAsync(() =>
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (headers != null)
            {
                foreach (var kv in headers)
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            if (body != null)
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return request;
        }, ct);

        var responseBody = await ReadBodyCappedAsync(response, ct);
        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

        return new HttpResponseDto(
            Status: (int)response.StatusCode,
            Headers: responseHeaders,
            Body: responseBody);
    }

    private async Task<HttpResponseMessage> SendWithRedirectValidationAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        using var template = createRequest();
        var method = template.Method;
        var headerPairs = template.Headers.ToList();
        HttpContent? content = template.Content;
        var currentUri = template.RequestUri
            ?? throw new InvalidOperationException("Request URI is required");

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await ValidateUrlAsync(currentUri.ToString(), ct);

            using var request = new HttpRequestMessage(method, currentUri);
            foreach (var h in headerPairs)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (content is not null && hop == 0)
                request.Content = content;

            var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!IsRedirectStatus(response.StatusCode) || hop == MaxRedirects)
                return response;

            var location = response.Headers.Location
                ?? throw new InvalidOperationException("Redirect response missing Location header");
            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (response.StatusCode == HttpStatusCode.SeeOther)
                method = HttpMethod.Get;
            response.Dispose();
        }

        throw new InvalidOperationException($"Too many redirects (>{MaxRedirects})");
    }

    private static bool IsRedirectStatus(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadBodyCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var sb = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (sb.Length + read > MaxResponseBytes)
                throw new InvalidOperationException(
                    $"Response body exceeds {MaxResponseBytes} byte limit");
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static void ValidateHttpMethod(string method)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"
        };
        if (!allowed.Contains(method))
            throw new ArgumentException(
                $"HTTP method '{method}' is not allowed; expected GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS");
    }

    private async Task ValidateUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid URL format");

        // Review F5: the scheme is decided before the address. HttpClient refuses ftp:, file:,
        // data:, ws: and the rest with a NotSupportedException the client never sees, and a
        // scheme with no host would otherwise resolve "" — this machine — for the address check.
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException(
                $"Unsupported URL scheme '{uri.Scheme}' in '{url}': only http and https are fetched.", nameof(url));

        if (_allowPrivateIps) return;

        // Resolve hostname and check ALL resolved IPs (defends against DNS rebinding)
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Cannot resolve hostname '{uri.Host}': {ex.Message}");
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"Hostname '{uri.Host}' did not resolve to any address");

        foreach (var addr in addresses)
        {
            if (IsPrivateAddress(addr))
                throw new InvalidOperationException(
                    $"URL targets a private IP address; refusing (resolved: {addr})");
        }
    }

    private static async ValueTask<Stream> BlockPrivateConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        foreach (var addr in addresses)
        {
            if (IsPrivateAddress(addr))
                throw new InvalidOperationException(
                    $"Connection to private IP refused ({addr})");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // internal for white-box testing of the SSRF range logic (InternalsVisibleTo).
    internal static bool IsPrivateAddress(IPAddress addr)
    {
        // Normalize IPv4-mapped IPv6 addresses (e.g. ::ffff:127.0.0.1)
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            // 127.0.0.0/8 — loopback
            if (bytes[0] == 127) return true;
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12 — 172.16.x.x to 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 — link-local / cloud metadata
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 100.64.0.0/10 — CGNAT / shared address space
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 198.18.0.0/15 — benchmark testing (often used for captive portals)
            if (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19) return true;
            return false;
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 — IPv6 loopback
            if (addr.Equals(IPAddress.IPv6Loopback)) return true;
            var bytes = addr.GetAddressBytes();
            // fc00::/7 — unique local (fc00:: and fd00::)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // fe80::/10 — link-local
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
            return false;
        }

        return false;
    }
}
