using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WebService : IWebService
{
    private const int MaxResponseBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRedirects = 5;

    private readonly HttpClient _client;
    private readonly bool _allowPrivateIps;
    private readonly ILogger? _log;

    /// <summary>Production constructor: SSRF protection is active (allowPrivateIps = false).</summary>
    public WebService(ILogger<WebService>? log = null)
        : this(allowPrivateIps: false, log) { }

    /// <summary>
    /// Test-accessible constructor. Set allowPrivateIps: true when using LocalHttpServerFixture
    /// (which binds to 127.0.0.1, otherwise blocked by SSRF protection).
    /// </summary>
    public WebService(bool allowPrivateIps, ILogger<WebService>? log = null)
    {
        _allowPrivateIps = allowPrivateIps;
        _log = log;
        _client = CreateClient(allowPrivateIps);
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

    public async Task<string> ScrapeAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var response = await SendWithRedirectValidationAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        var html = await ReadBodyCappedAsync(response, ct);
        var converter = new ReverseMarkdown.Converter();
        return converter.Convert(html);
    }

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
        if (_allowPrivateIps) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid URL format");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URL scheme '{uri.Scheme}' is not allowed");

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
