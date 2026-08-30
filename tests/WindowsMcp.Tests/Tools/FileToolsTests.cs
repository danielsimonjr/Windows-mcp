using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class FileToolsTests
{
    private static FileTools MakeTools(
        IFileSystemService? fs = null,
        IInputService? input = null,
        IFileStreamService? streams = null)
    {
        return new FileTools(
            fs      ?? new Mock<IFileSystemService>().Object,
            input   ?? new Mock<IInputService>().Object,
            streams ?? new Mock<IFileStreamService>().Object);
    }

    [Fact]
    public async Task FileWrite_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        Func<Task> act = () => tools.FileWrite(@"C:\tmp\file.txt", "hello", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.WriteTextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FileManage_delete_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        Func<Task> act = () => tools.FileManage("delete", @"C:\tmp\file.txt", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileSearch_passes_args_to_service()
    {
        var mock = new Mock<IFileSystemService>();
        var isoDate = "2024-01-15T10:00:00Z";
        var expectedDate = DateTime.Parse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind);

        mock.Setup(s => s.SearchAsync(
                @"C:\data", "*.txt", null, It.Is<DateTime?>(d => d.HasValue && d.Value == expectedDate), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileSearchHit>());

        var tools = MakeTools(fs: mock.Object);
        var result = await tools.FileSearch(@"C:\data", "*.txt", modified_since: isoDate);

        result.Should().NotBeNull();
        mock.VerifyAll();
    }

    [Fact]
    public async Task FileManage_copy_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("copy", @"C:\a.txt", @"C:\b.txt", confirm: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.CopyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileManage_move_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("move", @"C:\a.txt", @"C:\b.txt", confirm: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.MoveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Archive_zip_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.Archive("zip", @"C:\src", @"C:\out.zip", confirm: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.ZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Archive_unzip_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.Archive("unzip", @"C:\in.zip", @"C:\dst", confirm: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.UnzipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileRead_dispatches()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ReadTextAsync(@"C:\f.txt", 1048576, "auto", It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello");
        var tools = MakeTools(fs: mock.Object);

        var result = await tools.FileRead(@"C:\f.txt");

        result.Should().Be("hello");
        mock.VerifyAll();
    }

    [Fact]
    public async Task FileHash_dispatches()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.HashFileAsync(@"C:\f.txt", "sha256", It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        var tools = MakeTools(fs: mock.Object);

        var result = await tools.FileHash(@"C:\f.txt");

        result.Should().Be("abc123");
        mock.VerifyAll();
    }

    [Fact]
    public async Task FileInfo_dispatches()
    {
        var now = DateTime.UtcNow;
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.GetInfoAsync(@"C:\f.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileInfoDto(@"C:\f.txt", 4, now, now, now, "Archive", false));
        var tools = MakeTools(fs: mock.Object);

        var json = await tools.FileInfo(@"C:\f.txt");

        json.Should().Contain("f.txt");
        mock.VerifyAll();
    }

    [Fact]
    public async Task FileStreams_dispatches()
    {
        var mock = new Mock<IFileStreamService>();
        mock.Setup(s => s.GetStreamsAsync(@"C:\f.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileStreamsDto(@"C:\f.txt", null, Array.Empty<AlternateStreamInfo>()));
        var tools = MakeTools(streams: mock.Object);

        var json = await tools.FileStreams(@"C:\f.txt");

        json.Should().Contain("f.txt");
        mock.VerifyAll();
    }

    [Fact]
    public async Task FileSearch_invalid_modified_since_throws()
    {
        var tools = MakeTools();
        var act = () => tools.FileSearch(@"C:\data", modified_since: "not-a-date");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*modified_since*");
    }

    [Fact]
    public async Task FileManage_unknown_action_throws()
    {
        var act = () => MakeTools().FileManage("explode", @"C:\a.txt");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*action*");
    }

    [Fact]
    public async Task Archive_unknown_action_throws()
    {
        var act = () => MakeTools().Archive("explode", @"C:\src", @"C:\dst", confirm: true);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*action*");
    }

    [Fact]
    public async Task FileDialog_types_path()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(s => s.TypeAsync(@"C:\file.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TypeResult(10));
        var tools = MakeTools(input: mock.Object);

        var result = await tools.FileDialog(@"C:\file.txt");

        result.Should().Contain("typed");
        mock.Verify(s => s.TypeAsync(@"C:\file.txt", It.IsAny<CancellationToken>()), Times.Once);
    }
}
