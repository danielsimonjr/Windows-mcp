using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class PathPolicyTests
{
    [Fact]
    public void Normalize_empty_throws()
    {
        var act = () => PathPolicy.Normalize("");
        act.Should().Throw<ArgumentException>().WithMessage("*empty*");
        act = () => PathPolicy.Normalize("   ");
        act.Should().Throw<ArgumentException>().WithMessage("*empty*");
    }

    [Fact]
    public void Normalize_device_path_throws()
    {
        var act = () => PathPolicy.Normalize(@"\\.\C:");
        act.Should().Throw<ArgumentException>().WithMessage("*Device*");
    }

    [Fact]
    public void Normalize_temp_file_path_returns_full_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wm-policy-{Guid.NewGuid():N}.txt");
        PathPolicy.Normalize(path).Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public void MaxSearchHits_is_10000()
        => PathPolicy.MaxSearchHits.Should().Be(10_000);
}
