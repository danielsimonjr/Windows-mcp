using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class PowerServiceTests
{
    [Fact]
    public async Task ExecuteAsync_unknown_action_throws()
    {
        var svc = new PowerService();
        var act = () => svc.ExecuteAsync("explode");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Unknown power action*");
    }
}
