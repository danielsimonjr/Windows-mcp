using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class NetworkToolsTests
{
    [Fact]
    public async Task Network_ping_dispatches_to_service()
    {
        var mockNetwork = new Mock<INetworkService>();
        mockNetwork
            .Setup(s => s.PingAsync("example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PingResult("example.com", true, 12L));

        var tools = new NetworkTools(mockNetwork.Object, new Mock<IFirewallService>().Object);
        var result = await tools.Network("ping", host: "example.com");

        result.Should().Contain("example.com");
        mockNetwork.Verify(s => s.PingAsync("example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Firewall_add_requires_confirm()
    {
        var mockFirewall = new Mock<IFirewallService>();
        var tools = new NetworkTools(new Mock<INetworkService>().Object, mockFirewall.Object);

        Func<Task> act = () => tools.Firewall(
            action: "add",
            name: "TestRule",
            direction: "Inbound",
            action_type: "Allow",
            port: 8080,
            confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockFirewall.Verify(s => s.AddAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Network_adapters_dispatches()
    {
        var mock = new Mock<INetworkService>();
        mock.Setup(s => s.ListAdaptersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new NetworkAdapterDto("lo", "Loopback", "Up", new[] { "127.0.0.1" }) });
        var tools = new NetworkTools(mock.Object, new Mock<IFirewallService>().Object);

        var json = await tools.Network("adapters");

        json.Should().Contain("Loopback");
        mock.Verify(s => s.ListAdaptersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Network_ports_dispatches()
    {
        var mock = new Mock<INetworkService>();
        mock.Setup(s => s.ListPortsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PortInfoDto("0.0.0.0", 135, "0.0.0.0", 0, "Listen", 4, "System") });
        var tools = new NetworkTools(mock.Object, new Mock<IFirewallService>().Object);

        var json = await tools.Network("ports");

        json.Should().Contain("135");
        mock.Verify(s => s.ListPortsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Network_dns_dispatches()
    {
        var mock = new Mock<INetworkService>();
        mock.Setup(s => s.DnsLookupAsync("example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "93.184.216.34" });
        var tools = new NetworkTools(mock.Object, new Mock<IFirewallService>().Object);

        var json = await tools.Network("dns", host: "example.com");

        json.Should().Contain("93.184.216.34");
        mock.Verify(s => s.DnsLookupAsync("example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Network_wifi_dispatches()
    {
        var mock = new Mock<INetworkService>();
        mock.Setup(s => s.GetWifiAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WifiInfoDto("Cafe", 80, "connected"));
        var tools = new NetworkTools(mock.Object, new Mock<IFirewallService>().Object);

        var json = await tools.Network("wifi");

        json.Should().Contain("Cafe").And.Contain("connected");
        mock.Verify(s => s.GetWifiAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Firewall_list_dispatches()
    {
        var mock = new Mock<IFirewallService>();
        mock.Setup(s => s.ListAsync(null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new FirewallRuleDto("R1", "Rule One", "True", "Inbound", "Allow") });
        var tools = new NetworkTools(new Mock<INetworkService>().Object, mock.Object);

        var json = await tools.Firewall("list");

        json.Should().Contain("Rule One");
        mock.Verify(s => s.ListAsync(null, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Firewall_remove_requires_confirm()
    {
        var mock = new Mock<IFirewallService>();
        var tools = new NetworkTools(new Mock<INetworkService>().Object, mock.Object);

        var act = () => tools.Firewall(action: "remove", name: "TestRule", confirm: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
