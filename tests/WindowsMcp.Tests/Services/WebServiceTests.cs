using System.Net;
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
}
