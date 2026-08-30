using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class EventLogServiceTests
{
    [Fact]
    public async Task QueryAsync_returns_entries_from_application_log()
    {
        var svc = new EventLogService();
        var entries = await svc.QueryAsync("Application", null, null, DateTime.UtcNow.AddDays(-30), 5);
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task QueryAsync_clamps_max_to_1_through_1000()
    {
        var svc = new EventLogService();
        var tooLow = await svc.QueryAsync("Application", null, null, DateTime.UtcNow.AddYears(-20), 0);
        tooLow.Length.Should().BeLessThanOrEqualTo(1);

        var tooHigh = await svc.QueryAsync("Application", null, null, DateTime.UtcNow.AddYears(-20), 99999);
        tooHigh.Length.Should().BeLessThanOrEqualTo(1000);
    }
}
