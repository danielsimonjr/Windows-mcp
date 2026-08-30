using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class EnvServiceTests
{
    [Fact]
    public async Task Set_Get_List_roundtrip_a_process_variable()
    {
        var name = "WMCP_TEST_" + Guid.NewGuid().ToString("N");
        var svc = new EnvService();
        try
        {
            await svc.SetAsync(name, "hello", EnvironmentVariableTarget.Process);
            (await svc.GetAsync(name, EnvironmentVariableTarget.Process)).Should().Be("hello");

            var list = await svc.ListAsync(EnvironmentVariableTarget.Process);
            list.Should().ContainKey(name).WhoseValue.Should().Be("hello");
        }
        finally
        {
            await svc.SetAsync(name, null, EnvironmentVariableTarget.Process);
        }
    }
}
