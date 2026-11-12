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

    /// <summary>E-1: the second gated firewall action - "Some changes are refused without confirm: true".</summary>
    [Fact]
    public async Task Firewall_remove_requires_confirm()
    {
        var mockFirewall = new Mock<IFirewallService>();
        var tools = new NetworkTools(new Mock<INetworkService>().Object, mockFirewall.Object);

        Func<Task> act = () => tools.Firewall(action: "remove", name: "TestRule", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockFirewall.VerifyNoOtherCalls();
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
}
