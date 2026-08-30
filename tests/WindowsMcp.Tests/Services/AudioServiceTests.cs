using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class AudioServiceTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 50)]
    [InlineData(1f, 100)]
    public void ScalarToPercent_maps_unit_interval(float scalar, int expected)
        => AudioService.ScalarToPercent(scalar).Should().Be(expected);

    [Theory]
    [InlineData(-0.5f, 0)]
    [InlineData(1.5f, 100)]
    public void ScalarToPercent_clamps_out_of_range(float scalar, int expected)
        => AudioService.ScalarToPercent(scalar).Should().Be(expected);

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(50, 0.5f)]
    [InlineData(100, 1f)]
    public void PercentToScalar_maps_0_50_100(int percent, float expected)
        => AudioService.PercentToScalar(percent).Should().BeApproximately(expected, 0.0001f);

    [Fact]
    public void PercentToScalar_clamps_above_100()
        => AudioService.PercentToScalar(150).Should().Be(1f);
}
