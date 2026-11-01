using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class SecurityToolsTests
{
    [Fact]
    public void VerifySignature_serializes_the_inspector_verdict()
    {
        var inspector = new Mock<IAuthenticodeInspector>();
        inspector.Setup(i => i.Inspect(@"C:\app.exe"))
                 .Returns(new AuthenticodeInfo(true, "CN=Contoso"));
        var tools = new SecurityTools(inspector.Object, new Mock<ISecurityService>().Object, new Mock<ICertStoreService>().Object);

        var json = tools.VerifySignature(@"C:\app.exe");

        json.Should().Contain("true").And.Contain("Contoso");
    }

    [Fact]
    public void VerifySignature_forwards_the_path_to_the_inspector()
    {
        var inspector = new Mock<IAuthenticodeInspector>();
        inspector.Setup(i => i.Inspect(It.IsAny<string>()))
                 .Returns(new AuthenticodeInfo(false, null));
        var tools = new SecurityTools(inspector.Object, new Mock<ISecurityService>().Object, new Mock<ICertStoreService>().Object);

        tools.VerifySignature(@"C:\unknown.bin");

        inspector.Verify(i => i.Inspect(@"C:\unknown.bin"), Times.Once);
    }

    [Fact]
    public async Task DefenderStatus_serializes()
    {
        var security = new Mock<ISecurityService>();
        security.Setup(s => s.GetDefenderStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DefenderStatusDto(true, true, true, true, "1.2.3", null, null, null));
        var tools = new SecurityTools(new Mock<IAuthenticodeInspector>().Object, security.Object, new Mock<ICertStoreService>().Object);

        var json = await tools.DefenderStatus();

        json.Should().Contain("true").And.Contain("1.2.3");
        security.Verify(s => s.GetDefenderStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CertStore_dispatches()
    {
        var store = new Mock<ICertStoreService>();
        store.Setup(s => s.ListAsync("LocalMachine", "Root", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new CertInfoDto("CN=Test", "CN=Test", "ABC", DateTime.UtcNow.AddYears(1), true, false)
            });
        var tools = new SecurityTools(new Mock<IAuthenticodeInspector>().Object, new Mock<ISecurityService>().Object, store.Object);

        var json = await tools.CertStore();

        json.Should().Contain("ABC").And.Contain("CN=Test");
        store.Verify(s => s.ListAsync("LocalMachine", "Root", It.IsAny<CancellationToken>()), Times.Once);
    }
}
