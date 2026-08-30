using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class NotificationServiceTests
{
    [Fact]
    public void EscapeXml_escapes_ampersand_angles_and_quotes()
    {
        NotificationService.EscapeXml(@"Tom & Jerry <fun> ""quoted"" 'apos'")
            .Should().Be("Tom &amp; Jerry &lt;fun&gt; &quot;quoted&quot; &apos;apos&apos;");
    }

    [Fact]
    public async Task ShowAsync_forwards_a_script_containing_the_escaped_title()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(s => s.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PSResult(true, "", "", 0, Array.Empty<string>()));

        var svc = new NotificationService(ps.Object);
        await svc.ShowAsync("A & B <C>", "hello");

        ps.Verify(s => s.RunAsync(
            It.Is<string>(script => script.Contains("A &amp; B &lt;C&gt;")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
