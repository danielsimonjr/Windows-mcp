using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class WebTools
{
    private readonly IWebService _web;

    public WebTools(IWebService web)
    {
        _web = web;
    }

    [McpServerTool, Description("Fetch a URL and convert HTML to markdown.")]
    public async Task<string> Scrape(
        [Description("Public URL (private IPs rejected)")] string url)
        => await _web.ScrapeAsync(url);

    [McpServerTool, Description("Make an HTTP request to a URL.")]
    public async Task<string> HttpRequest(
        string url,
        [Description("GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS")] string method = "GET",
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
                throw new ArgumentException($"Invalid headers_json: {ex.Message}");
            }
        }
        var result = await _web.RequestAsync(url, method, headers, body);
        return JsonSerializer.Serialize(result);
    }
}
