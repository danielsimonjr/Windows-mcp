using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class WebServiceTests
{
    [Theory]
    [InlineData("127.0.0.1")]    // loopback
    [InlineData("10.1.2.3")]     // 10/8
    [InlineData("172.16.0.1")]   // 172.16/12 low
    [InlineData("172.31.255.1")] // 172.16/12 high
    [InlineData("192.168.1.1")]  // 192.168/16
    [InlineData("169.254.1.1")]  // link-local
    [InlineData("0.0.0.0")]      // 0/8
    [InlineData("::1")]          // IPv6 loopback
    [InlineData("fc00::1")]      // unique-local
    [InlineData("fd12:3456::1")] // unique-local
    [InlineData("fe80::1")]      // IPv6 link-local
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback (DNS-rebinding evasion)
    public void IsPrivateAddress_flags_private_and_loopback_ranges(string ip)
        => WebService.IsPrivateAddress(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]   // just below 172.16/12
    [InlineData("172.32.0.1")]   // just above 172.16/12
    [InlineData("2606:4700:4700::1111")] // public IPv6 (Cloudflare)
    public void IsPrivateAddress_allows_public_addresses(string ip)
        => WebService.IsPrivateAddress(IPAddress.Parse(ip)).Should().BeFalse();

    [Fact]
    public async Task ScrapeAsync_blocks_a_loopback_url()
    {
        var svc = new WebService(); // production ctor: SSRF protection on
        var act = () => svc.ScrapeAsync("http://127.0.0.1/secret");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*private IP*");
    }

    [Theory]
    [InlineData("100.64.0.1")]   // CGNAT / shared address space
    [InlineData("100.127.255.1")]
    [InlineData("198.18.0.1")]   // benchmark testing range
    [InlineData("198.19.255.1")]
    public void IsPrivateAddress_flags_additional_reserved_ranges(string ip)
        => WebService.IsPrivateAddress(IPAddress.Parse(ip)).Should().BeTrue();

    [Fact]
    public async Task ScrapeAsync_rejects_unresolvable_hostname()
    {
        var svc = new WebService();
        var act = () => svc.ScrapeAsync("http://this-host-definitely-does-not-exist-xyz123.invalid/");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolve*");
    }

    [Fact]
    public async Task ScrapeAsync_rejects_a_malformed_url()
    {
        var svc = new WebService();
        var act = () => svc.ScrapeAsync("not-a-url");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid URL*");
    }

    // ---- F5: only http and https ---------------------------------------------------------------

    /// <summary>
    /// F5: every other scheme is refused by name, before anything is resolved or opened.
    /// <c>file:</c> and <c>data:</c> are the ones that matter — a URL is model-supplied input, and
    /// "fetch this URL" must never become "read this local file" — but the refusal is the same for
    /// any scheme the tool cannot serve, because <c>HttpClient</c>'s own
    /// <c>NotSupportedException</c> is not caller-facing and reaches the model as the SDK's
    /// "An error occurred invoking 'scrape'.", which tells it nothing and invites a retry.
    /// </summary>
    /// <remarks>
    /// <c>allowPrivateIps: true</c> is the loopback-test constructor, which short-circuits the
    /// address check entirely: the scheme refusal has to fire regardless, so this also pins that it
    /// is not hidden behind the SSRF guard. Nothing here touches the network.
    /// </remarks>
    [Theory]
    [InlineData("ftp://ftp.example.com/x", "ftp")]
    [InlineData("file:///C:/Windows/win.ini", "file")]
    [InlineData("data:text/plain,hi", "data")]
    [InlineData("ws://example.com/", "ws")]
    [InlineData("javascript:alert(1)", "javascript")]
    public async Task ScrapeAsync_refuses_any_scheme_but_http_and_https(string url, string scheme)
    {
        var svc = new WebService(allowPrivateIps: true);

        Func<Task> act = () => svc.ScrapeAsync(url);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain(scheme, "the caller is told which scheme was refused");
        message.Should().Contain("http").And.Contain("https", "and which two it can use instead");
    }

    [Theory]
    [InlineData("ftp://ftp.example.com/x", "ftp")]
    [InlineData("file:///C:/Windows/win.ini", "file")]
    [InlineData("data:text/plain,hi", "data")]
    [InlineData("ws://example.com/", "ws")]
    [InlineData("javascript:alert(1)", "javascript")]
    public async Task RequestAsync_refuses_any_scheme_but_http_and_https(string url, string scheme)
    {
        var svc = new WebService(allowPrivateIps: true);

        Func<Task> act = () => svc.RequestAsync(url, "GET", null, null);

        var message = (await act.Should().ThrowAsync<ArgumentException>(
            "http_request shares the check: the two tools take the same model-supplied URL")).Which.Message;
        message.Should().Contain(scheme);
        message.Should().Contain("http").And.Contain("https");
    }

    /// <summary>
    /// F5: the scheme is decided BEFORE the address, so the answer names the thing that cannot be
    /// changed by choosing a different host. <c>ftp://127.0.0.1</c> breaks both rules; reporting
    /// "private IP" would send the caller off to find a public FTP server.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_refuses_the_scheme_before_it_looks_at_the_address()
    {
        var svc = new WebService();   // production ctor: the SSRF guard is on

        Func<Task> act = () => svc.ScrapeAsync("ftp://127.0.0.1/secret");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("ftp");
    }

    // ---- C-5 (R8): the title the scrape result carries -----------------------------------------

    [Fact]
    public void HtmlTitle_reads_the_documents_title()
        => WebService.HtmlTitle("<html><head><title>One</title></head><body>x</body></html>")
            .Should().Be("One");

    /// <summary>
    /// F11: the page's own title is the first HTML <c>&lt;title&gt;</c> ELEMENT. "The first
    /// <c>&lt;title&gt;</c> in the text" is a different rule that happens to agree here and
    /// disagrees in every case below — a page whose SVG icon, HTML comment or inline script
    /// mentions a title before the real one is titled by the wrong string.
    /// </summary>
    [Fact]
    public void HtmlTitle_takes_the_first_title_element_and_ignores_any_later_one()
        => WebService.HtmlTitle("<head><title>First</title><title>Second</title></head><body>x</body>")
            .Should().Be("First", "a second title element does not rename the page");

    [Fact]
    public void HtmlTitle_ignores_an_svg_title_that_comes_before_the_pages_own()
        => WebService.HtmlTitle(
                "<html><body><svg><title>Menu icon</title></svg></body><head><title>Real</title></head></html>")
            .Should().Be("Real",
                "an SVG <title> is a tooltip on a graphic, not the document's title - and it is "
                + "routinely the first one in the markup of an icon-led page");

    [Fact]
    public void HtmlTitle_of_a_page_whose_only_title_is_an_svg_tooltip_is_null()
        => WebService.HtmlTitle("<html><body><svg><title>Menu icon</title></svg></body></html>")
            .Should().BeNull("naming the page after a menu icon is worse than saying it has no title");

    [Fact]
    public void HtmlTitle_ignores_a_title_inside_a_comment()
        => WebService.HtmlTitle("<html><head><!-- <title>Commented</title> --><title>Real</title></head></html>")
            .Should().Be("Real", "commented-out markup is not markup - a leftover draft title is not the page's");

    [Fact]
    public void HtmlTitle_ignores_a_title_inside_a_script_string()
        => WebService.HtmlTitle(
                "<html><head><script>var t = \"<title>Scripted</title>\";</script><title>Real</title></head></html>")
            .Should().Be("Real", "a string inside a script is data, not an element");

    [Fact]
    public void HtmlTitle_decodes_the_entities()
        => WebService.HtmlTitle("<title>A &amp; B &lt;C&gt; &#39;D&#39; &quot;E&quot;</title>")
            .Should().Be("A & B <C> 'D' \"E\"", "the caller gets the title as a reader sees it, not as HTML");

    [Theory]
    [InlineData("<title>  A   B  </title>", "A B")]
    [InlineData("<title>A\nB</title>", "A B")]
    [InlineData("<title>A\r\nB</title>", "A B")]
    [InlineData("<title>\tA\tB\t</title>", "A B")]
    public void HtmlTitle_collapses_the_whitespace_and_trims_the_ends(string html, string expected)
        => WebService.HtmlTitle(html).Should().Be(expected);

    [Fact]
    public void HtmlTitle_decodes_before_it_collapses()
        => WebService.HtmlTitle("<title>A&#10;&#10;B</title>").Should().Be("A B",
            "a newline that arrived as an entity is still whitespace once it is decoded");

    [Theory]
    [InlineData("<title lang=\"en\">One</title>")]
    [InlineData("<title id='t' class=\"a b\">One</title>")]
    [InlineData("<TITLE>One</TITLE>")]
    [InlineData("<Title>One</title>")]
    public void HtmlTitle_reads_the_tag_whatever_its_case_or_attributes(string html)
        => WebService.HtmlTitle(html).Should().Be("One");

    [Theory]
    [InlineData("<html><body><h1>Hello</h1></body></html>")]        // no title at all
    [InlineData("<html><head></head><body>x</body></html>")]
    [InlineData("")]
    public void HtmlTitle_of_a_page_with_no_title_is_null(string html)
        => WebService.HtmlTitle(html).Should().BeNull("null says 'this page has no title', which \"\" does not");

    [Theory]
    [InlineData("<title></title>")]
    [InlineData("<title>   </title>")]
    [InlineData("<title>\n\t </title>")]
    [InlineData("<title>&#32;</title>")]
    public void HtmlTitle_of_a_blank_title_is_null(string html)
        => WebService.HtmlTitle(html).Should().BeNull("a title of nothing but whitespace is not a title");

    /// <summary>
    /// F11 changes this one: a parser closes an unterminated <c>&lt;title&gt;</c> at end of input,
    /// exactly as a browser does, so the page IS titled — where the old text match, having no
    /// closing tag to stop at, reported it untitled. The title a reader would see is the honest
    /// answer for a page that ends inside its own head.
    /// </summary>
    [Fact]
    public void HtmlTitle_of_an_unclosed_title_is_closed_at_the_end_of_the_document()
        => WebService.HtmlTitle("<html><head><title>One").Should().Be("One",
            "the document ends there, so 'One' is the whole of the title - the same string a "
            + "browser puts in the tab");

    // ---- F9: NestingDepth, the measurement the stack-overflow guard is made of ------------------

    private static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>
    /// F9: the guard refuses a document deeper than <see cref="WebService.MaxNestingDepth"/>, so
    /// what the number counts decides which pages are refused. It is the depth BELOW
    /// <c>&lt;body&gt;</c>: body's own children are level 1. Counting from
    /// <c>&lt;html&gt;</c> instead shifts every page by two and refuses the last two legal levels.
    /// </summary>
    [Theory]
    [InlineData("<html><body><p>x</p></body></html>", 1)]
    [InlineData("<html><body><div><p>x</p></div></body></html>", 2)]
    [InlineData("<html><body><div><div><div>x</div></div></div></body></html>", 3)]
    public void NestingDepth_counts_the_levels_below_body_with_its_own_children_at_one(string html, int expected)
        => WebService.NestingDepth(Parse(html)).Should().Be(expected);

    /// <summary>The guard's own boundary, on the same page shape the fixture serves over HTTP.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(299)]
    [InlineData(300)]
    [InlineData(301)]
    public void NestingDepth_of_n_nested_divs_is_n(int levels)
        => WebService.NestingDepth(Parse(Fixtures.LocalHttpServerFixture.DeepPage(levels)))
                     .Should().Be(levels, "the converter recurses once per level below body, and no more");

    [Fact]
    public void NestingDepth_at_the_limit_is_allowed_and_one_past_it_is_not()
    {
        WebService.NestingDepth(Parse(Fixtures.LocalHttpServerFixture.DeepPage(WebService.MaxNestingDepth)))
                  .Should().Be(WebService.MaxNestingDepth, "the last legal document is not refused");
        WebService.NestingDepth(Parse(Fixtures.LocalHttpServerFixture.DeepPage(WebService.MaxNestingDepth + 1)))
                  .Should().BeGreaterThan(WebService.MaxNestingDepth, "and the first illegal one is");
    }

    /// <summary>
    /// The converter recurses over ELEMENTS. Text, comments and attributes cost no stack, so a
    /// page of prose in one paragraph is depth 1 however long it is — counting nodes instead
    /// would refuse ordinary pages.
    /// </summary>
    [Theory]
    [InlineData("<html><body>just text</body></html>", 0)]
    [InlineData("<html><body><div>text<!-- a comment --></div></body></html>", 1)]
    [InlineData("<html><body><p>a<br>b<br>c</p></body></html>", 2)]
    public void NestingDepth_counts_elements_and_not_text_or_comment_nodes(string html, int expected)
        => WebService.NestingDepth(Parse(html)).Should().Be(expected);

    /// <summary>
    /// The walk starts at <c>&lt;body&gt;</c>, so a deep <c>&lt;head&gt;</c> does not count — the
    /// converter is handed the body's content. The head is nested here through the DOM rather
    /// than through markup on purpose: the HTML parser moves flow content out of <c>&lt;head&gt;</c>
    /// into the body, so a hand-written "deep head" page is really a deep BODY page (measured:
    /// four <c>&lt;div&gt;</c>s inside a <c>&lt;noscript&gt;</c> in the head are re-parented and
    /// counted, correctly, as depth 4).
    /// </summary>
    [Fact]
    public void NestingDepth_does_not_count_the_head_however_deep_it_is()
    {
        var document = Parse("<html><head><title>t</title></head><body><p>x</p></body></html>");
        IElement node = document.Head!;
        for (var i = 0; i < 10; i++)
        {
            var child = document.CreateElement("div");
            node.AppendChild(child);
            node = child;
        }

        WebService.NestingDepth(document).Should().Be(1, "only what is below body is converted");
    }

    /// <summary>The deepest chain, not the widest row: 500 siblings one level down are still 1.</summary>
    [Fact]
    public void NestingDepth_reports_the_deepest_chain_not_the_number_of_siblings()
    {
        var wide = "<html><body>" + string.Concat(Enumerable.Repeat("<p>x</p>", 500)) + "</body></html>";

        WebService.NestingDepth(Parse(wide)).Should().Be(1);
    }

    /// <summary>
    /// A document with no <c>&lt;body&gt;</c> element: there is nothing for the converter to walk,
    /// so the depth is 0 and the page is not refused. A guard that dereferenced the missing body
    /// would take the call out with a <c>NullReferenceException</c>, which the client only ever
    /// sees as "An error occurred invoking 'scrape'." The body is removed rather than parsed away
    /// because the HTML parser always supplies one (see the frameset case below), so this is the
    /// only shape that reaches the guard.
    /// </summary>
    [Fact]
    public void NestingDepth_of_a_document_with_no_body_is_zero()
    {
        var document = Parse("<html><body><div><p>x</p></div></body></html>");
        WebService.NestingDepth(document).Should().Be(2, "the body carries depth before it is removed");

        document.Body!.Remove();

        document.Body.Should().BeNull("nothing is left for the converter to walk");
        WebService.NestingDepth(document).Should().Be(0);
    }

    /// <summary>
    /// A frameset page has no <c>&lt;body&gt;</c> in its markup, but the HTML standard resolves
    /// <c>document.body</c> to "the first body OR frameset child", and AngleSharp does the same —
    /// so the frames themselves are what gets measured (depth 1 here). Pinned because it is the
    /// obvious candidate for "a document with no body" and it is not one.
    /// </summary>
    [Fact]
    public void NestingDepth_of_a_frameset_page_measures_the_frameset()
    {
        var document = Parse("<html><head><title>Frames</title></head>"
                             + "<frameset cols=\"50%,50%\"><frame src=\"a.htm\"><frame src=\"b.htm\"></frameset></html>");

        document.Body.Should().NotBeNull("the standard resolves body to the frameset");
        WebService.NestingDepth(document).Should().Be(1, "the two frames are its children");
    }
}
