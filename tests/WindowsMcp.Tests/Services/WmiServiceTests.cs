using System.Collections.Generic;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class WmiServiceTests
{
    [Fact]
    public async Task QueryAsync_returns_os_caption_row()
    {
        var svc = new WmiService();

        var rows = await svc.QueryAsync("Win32_OperatingSystem");

        rows.Should().ContainSingle();
        var props = rows[0].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
        props.Should().ContainKey("Caption");
        props["Caption"].Should().NotBeNull();
    }

    [Fact]
    public async Task QueryAsync_applies_a_where_clause()
    {
        var svc = new WmiService();

        // The fixed drive that always exists; DriveType=3 is a local disk.
        var rows = await svc.QueryAsync("Win32_LogicalDisk", where: "DeviceID='C:'");

        rows.Should().ContainSingle();
        var props = (IDictionary<string, object>)rows[0];
        props["DeviceID"].Should().Be("C:");
    }

    [Theory]
    [InlineData("Win32_Process; DROP TABLE", null)]
    [InlineData("Win32_Process", "root\\cimv2; evil")]
    [InlineData("Win32_Process", null, "Name='x'; DELETE FROM Win32_Process")]
    public void ValidateQuery_rejects_injection_patterns(string className, string? ns = "root\\cimv2", string? where = null)
    {
        var act = () => WmiService.ValidateQuery(className, ns, where);
        act.Should().Throw<ArgumentException>();
    }
}
