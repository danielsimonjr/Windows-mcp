using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

public class WebToolsTests : IClassFixture<LocalHttpServerFixture>
{
    private readonly LocalHttpServerFixture _fixture;

    public WebToolsTests(LocalHttpServerFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Scrape_converts_html_to_markdown()
    {
        var tools = new WebTools(new WebService(allowPrivateIps: true));
        var md = await tools.Scrape(_fixture.UrlFor("/"));
        // ReverseMarkdown may render h1 as "# Hello" or just "Hello"
        md.Should().Contain("Hello");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Scrape_rejects_private_IPs_by_default()
    {
        var tools = new WebTools(new WebService());  // default allowPrivateIps: false
        Func<Task> act = () => tools.Scrape("http://127.0.0.1:80/admin");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*private*");
    }

    [Fact]
    [Trait("Category", "Unit")]
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

        var tools = new WebTools(mockWeb.Object);
        var json = await tools.HttpRequest("https://example.com", "GET");

        json.Should().Contain("200");
        json.Should().Contain("Test");
        mockWeb.VerifyAll();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HttpRequest_invalid_headers_json_throws()
    {
        var tools = new WebTools(new Mock<IWebService>().Object);
        var act = () => tools.HttpRequest("https://example.com", headers_json: "not-json");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*headers_json*");
    }
}
