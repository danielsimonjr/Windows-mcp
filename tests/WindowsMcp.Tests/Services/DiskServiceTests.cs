using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class DiskServiceTests
{
    private static PSResult Ok(string stdout) => new(true, stdout, "", 0, Array.Empty<string>());

    private static DiskService Make(Mock<IFileSystemService>? fs = null, Mock<IPowerShellService>? ps = null)
        => new((fs ?? new Mock<IFileSystemService>()).Object, (ps ?? new Mock<IPowerShellService>()).Object);

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_610_612_736, "1.5 GB")]
    public void FormatBytes_scales_units(long bytes, string expected)
        => DiskService.FormatBytes(bytes).Should().Be(expected);

    [Fact]
    public void GetTopLevelDir_returns_first_segment_under_root()
        => DiskService.GetTopLevelDir(@"C:\", @"C:\Windows\System32\x.dll")
            .Should().Be(@"C:\Windows");

    [Fact]
    public void GetTopLevelDir_returns_root_for_a_file_directly_in_root()
        => DiskService.GetTopLevelDir(@"C:\", @"C:\pagefile.sys")
            .Should().Be(@"C:\");

    [Fact]
    public async Task GetUsageAsync_groups_by_top_dir_sorted_desc()
    {
        var fs = new Mock<IFileSystemService>();
        fs.Setup(f => f.SearchAsync(@"C:\", "*", null, null, false, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new[]
          {
              new FileSearchHit(@"C:\Windows\a", 100, DateTime.UtcNow),
              new FileSearchHit(@"C:\Windows\b", 200, DateTime.UtcNow),
              new FileSearchHit(@"C:\Users\c", 50, DateTime.UtcNow),
          });

        var result = await Make(fs).GetUsageAsync(@"C:\");

        result.Should().HaveCount(2);
        result[0].Dir.Should().Be(@"C:\Windows");
        result[0].SizeBytes.Should().Be(300);
        result[1].Dir.Should().Be(@"C:\Users");
    }

    [Fact]
    public async Task GetFileTypesAsync_groups_by_extension_sorted_desc()
    {
        var fs = new Mock<IFileSystemService>();
        fs.Setup(f => f.SearchAsync(@"C:\", "*", null, null, false, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new[]
          {
              new FileSearchHit(@"C:\a.dll", 300, DateTime.UtcNow),
              new FileSearchHit(@"C:\b.dll", 100, DateTime.UtcNow),
              new FileSearchHit(@"C:\c.txt", 50, DateTime.UtcNow),
              new FileSearchHit(@"C:\noext", 10, DateTime.UtcNow),
          });

        var result = await Make(fs).GetFileTypesAsync(@"C:\");

        result[0].Extension.Should().Be(".dll");
        result[0].Count.Should().Be(2);
        result[0].SizeBytes.Should().Be(400);
        result.Should().Contain(e => e.Extension == ".txt" && e.Count == 1);
        result.Should().Contain(e => e.Extension == "(none)" && e.SizeBytes == 10);
    }

    [Fact]
    public async Task GetStaleAsync_returns_only_files_older_than_threshold()
    {
        var fs = new Mock<IFileSystemService>();
        fs.Setup(f => f.SearchAsync(@"C:\", "*", null, null, false, It.IsAny<CancellationToken>()))
          .ReturnsAsync(new[]
          {
              new FileSearchHit(@"C:\old", 1, DateTime.UtcNow.AddDays(-400)),
              new FileSearchHit(@"C:\new", 1, DateTime.UtcNow.AddDays(-10)),
          });

        var result = await Make(fs).GetStaleAsync(@"C:\", olderThanDays: 365);

        result.Should().ContainSingle();
        result[0].Path.Should().Be(@"C:\old");
    }

    [Fact]
    public async Task GetReclaimableAsync_parses_powershell_json()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok("""{"TempBytes":100,"InetCacheBytes":200,"RecycleBinBytes":300,"TotalBytes":600}"""));

        var result = await Make(ps: ps).GetReclaimableAsync();

        result.TempBytes.Should().Be(100);
        result.TotalBytes.Should().Be(600);
    }

    [Fact]
    public async Task GetReclaimableAsync_throws_on_empty_output()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok(""));

        var act = () => Make(ps: ps).GetReclaimableAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no output*");
    }
}
