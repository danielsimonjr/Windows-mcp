using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ShellToolsTests
{
    [Fact]
    public async Task Powershell_requires_confirm_true()
    {
        var mock = new Mock<IPowerShellService>();
        var tools = new ShellTools(mock.Object);

        Func<Task> act = () => tools.Powershell("Get-Date", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Powershell_serializes_result_when_confirmed()
    {
        var mock = new Mock<IPowerShellService>();
        mock.Setup(s => s.RunAsync("Get-Date", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PSResult(true, "ok", "", 0, Array.Empty<string>()));
        var tools = new ShellTools(mock.Object);

        var json = await tools.Powershell("Get-Date", confirm: true);

        json.Should().Contain("ok");
        mock.Verify(s => s.RunAsync("Get-Date", It.IsAny<CancellationToken>()), Times.Once);
    }
}
