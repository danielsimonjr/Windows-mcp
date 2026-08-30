using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class UIAutomationToolsTests
{
    [Fact]
    public async Task FindElement_passes_interactive_kind_to_service()
    {
        var element = new ElementInfo("el-1", "Submit", "Button", true, false,
            new Bounds(10, 20, 100, 30), null, null, null);
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync("Submit", FindKind.Interactive, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FindElementResult(new[] { element }));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.FindElement("Submit", "interactive");

        result.Should().Contain("Submit").And.Contain("el-1");
        mock.VerifyAll();
    }

    [Fact]
    public async Task FindElement_rejects_unknown_kind_with_clear_message()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", "unknown_kind");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*kind*");
    }

    private static ElementInfo SampleElement() =>
        new("el-1", "Submit", "Button", true, false, new Bounds(10, 20, 100, 30), null, null, null);

    [Fact]
    public async Task GetState_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementTree(SampleElement(), Array.Empty<ElementTree>()));
        var tools = new UIAutomationTools(mock.Object);

        var json = await tools.GetState();

        json.Should().Contain("Submit");
        mock.Verify(s => s.GetStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetElement_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.GetElementAsync("el-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleElement());
        var tools = new UIAutomationTools(mock.Object);

        var json = await tools.GetElement("el-1");

        json.Should().Contain("el-1");
        mock.Verify(s => s.GetElementAsync("el-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetText_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.GetTextAsync("el-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello");
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.GetText("el-1");

        result.Should().Be("hello");
        mock.Verify(s => s.GetTextAsync("el-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InteractElement_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.InteractElement("el-1", "click");

        result.Should().Be("interacted");
        mock.Verify(s => s.InteractAsync("el-1", "click", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTable_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.GetTableAsync("el-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableData(new[] { "Name" }, new[] { new[] { "Ada" } }));
        var tools = new UIAutomationTools(mock.Object);

        var json = await tools.GetTable("el-1");

        json.Should().Contain("Ada");
        mock.Verify(s => s.GetTableAsync("el-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssertElement_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.AssertElementAsync("el-1", "enabled", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.AssertElement("el-1", "enabled");

        result.Should().Be("PASS");
        mock.Verify(s => s.AssertElementAsync("el-1", "enabled", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitFor_dispatches()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.WaitForAsync("Submit", 10000, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleElement());
        var tools = new UIAutomationTools(mock.Object);

        var json = await tools.WaitFor("Submit");

        json.Should().Contain("Submit");
        mock.Verify(s => s.WaitForAsync("Submit", 10000, 500, It.IsAny<CancellationToken>()), Times.Once);
    }
}
