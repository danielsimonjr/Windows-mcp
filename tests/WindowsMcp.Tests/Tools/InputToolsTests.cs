using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class InputToolsTests
{
    [Fact]
    public async Task Click_dispatches_to_service_with_correct_args()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(s => s.ClickAsync(100, 200, MouseButton.Left, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClickResult(100, 200, MouseButton.Left, 2));
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Click(100, 200, "left", 2);

        result.Should().Contain("100").And.Contain("200");
        mock.VerifyAll();
    }

    [Fact]
    public async Task Click_rejects_unknown_button_with_clear_message()
    {
        var tools = new InputTools(new Mock<IInputService>().Object, new Mock<IClipboardService>().Object);
        Func<Task> act = () => tools.Click(0, 0, "fourth", 1);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*button*");
    }

    [Fact]
    public async Task Drag_dispatches()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(s => s.DragAsync(1, 2, 3, 4, MouseButton.Left, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DragResult(1, 2, 3, 4, MouseButton.Left));
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Drag(1, 2, 3, 4);

        result.Should().Contain("1").And.Contain("3");
        mock.VerifyAll();
    }

    [Fact]
    public async Task Hover_dispatches()
    {
        var mock = new Mock<IInputService>();
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Hover(10, 20, 5);

        result.Should().Contain("10").And.Contain("20");
        mock.Verify(s => s.HoverAsync(10, 20, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Type_dispatches()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(s => s.TypeAsync("hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TypeResult(2));
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Type("hi");

        result.Should().Contain("2");
        mock.VerifyAll();
    }

    [Fact]
    public async Task Key_dispatches()
    {
        var mock = new Mock<IInputService>();
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Key("enter");

        result.Should().Contain("enter");
        mock.Verify(s => s.PressKeyAsync("enter", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shortcut_dispatches()
    {
        var mock = new Mock<IInputService>();
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Shortcut("ctrl+c");

        result.Should().Contain("ctrl+c");
        mock.Verify(s => s.PressShortcutAsync("ctrl+c", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scroll_dispatches()
    {
        var mock = new Mock<IInputService>();
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Scroll(1, 2, "down", 4);

        result.Should().Contain("down");
        mock.Verify(s => s.ScrollAsync(1, 2, "down", 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clipboard_get_dispatches()
    {
        var clip = new Mock<IClipboardService>();
        clip.Setup(s => s.GetTextAsync(It.IsAny<CancellationToken>())).ReturnsAsync("clip-text");
        var tools = new InputTools(new Mock<IInputService>().Object, clip.Object);

        var result = await tools.Clipboard("get");

        result.Should().Be("clip-text");
        clip.Verify(s => s.GetTextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clipboard_set_dispatches()
    {
        var clip = new Mock<IClipboardService>();
        var tools = new InputTools(new Mock<IInputService>().Object, clip.Object);

        var result = await tools.Clipboard("set", "hello");

        result.Should().Contain("5");
        clip.Verify(s => s.SetTextAsync("hello", It.IsAny<CancellationToken>()), Times.Once);
    }
}
