using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class DiskToolsTests
{
    [Fact]
    public async Task DiskInspect_rejects_unknown_mode()
    {
        var tools = new DiskTools(new Mock<IDiskService>().Object);

        Func<Task> act = () => tools.DiskInspect("bogus_mode", @"C:\");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mode*");
    }

    [Fact]
    public async Task DiskInspect_usage_serializes_concrete_entries_not_empty_objects()
    {
        var disk = new Mock<IDiskService>();
        disk.Setup(d => d.GetUsageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new DiskUsageEntry(@"C:\Windows", 2048, "2.0 KB") });
        var tools = new DiskTools(disk.Object);

        var json = await tools.DiskInspect("usage", @"C:\");

        // Regression guard for the JsonSerializer.Serialize(object) -> "{}" trap.
        json.Should().Contain("Windows").And.Contain("2048");
    }

    [Fact]
    public async Task DiskInspect_reclaimable_dispatches()
    {
        var disk = new Mock<IDiskService>();
        disk.Setup(d => d.GetReclaimableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReclaimableSpace(1, 2, 3, 6));
        var tools = new DiskTools(disk.Object);

        var json = await tools.DiskInspect("reclaimable");

        json.Should().Contain("6");
        disk.Verify(d => d.GetReclaimableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiskInspect_file_types_dispatches()
    {
        var disk = new Mock<IDiskService>();
        disk.Setup(d => d.GetFileTypesAsync(@"C:\", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new FileTypeEntry(".txt", 2, 10, "10 B") });
        var tools = new DiskTools(disk.Object);

        var json = await tools.DiskInspect("file_types");

        json.Should().Contain(".txt");
        disk.Verify(d => d.GetFileTypesAsync(@"C:\", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiskInspect_stale_dispatches()
    {
        var disk = new Mock<IDiskService>();
        disk.Setup(d => d.GetStaleAsync(@"C:\", 365, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new StaleFileEntry(@"C:\old.txt", 10, "10 B", DateTime.UtcNow) });
        var tools = new DiskTools(disk.Object);

        var json = await tools.DiskInspect("stale");

        json.Should().Contain("old.txt");
        disk.Verify(d => d.GetStaleAsync(@"C:\", 365, It.IsAny<CancellationToken>()), Times.Once);
    }
}
