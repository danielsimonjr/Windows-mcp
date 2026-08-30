using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class SystemToolsTests
{
    private static SystemTools MakeTools(
        IWmiService? wmi = null,
        IEnvService? env = null,
        IPowerService? power = null,
        INotificationService? notification = null,
        IAudioService? audio = null,
        ISecurityService? security = null,
        IReliabilityService? reliability = null,
        IDriverService? drivers = null)
    {
        return new SystemTools(
            wmi          ?? new Mock<IWmiService>().Object,
            env          ?? new Mock<IEnvService>().Object,
            power        ?? new Mock<IPowerService>().Object,
            notification ?? new Mock<INotificationService>().Object,
            audio        ?? new Mock<IAudioService>().Object,
            security     ?? new Mock<ISecurityService>().Object,
            reliability  ?? new Mock<IReliabilityService>().Object,
            drivers      ?? new Mock<IDriverService>().Object);
    }

    [Fact]
    public async Task PowerAction_requires_confirm()
    {
        var mockPower = new Mock<IPowerService>();
        var tools = MakeTools(power: mockPower.Object);

        Func<Task> act = () => tools.PowerAction("shutdown", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockPower.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SystemInfo_dispatches_to_wmi_with_correct_class()
    {
        var mockWmi = new Mock<IWmiService>();
        mockWmi.Setup(s => s.QueryAsync("Win32_OperatingSystem", null, null, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<object>());

        var tools = MakeTools(wmi: mockWmi.Object);
        var result = await tools.SystemInfo("os");

        result.Should().NotBeNull();
        mockWmi.Verify(s => s.QueryAsync("Win32_OperatingSystem", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Env_set_requires_confirm()
    {
        var mockEnv = new Mock<IEnvService>();
        var tools = MakeTools(env: mockEnv.Object);

        Func<Task> act = () => tools.Env("set", name: "MY_VAR", value: "hello", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockEnv.Verify(s => s.SetAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<EnvironmentVariableTarget>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Audio_set_requires_level()
    {
        var mock = new Mock<IAudioService>();
        var tools = MakeTools(audio: mock.Object);

        var act = () => tools.Audio("set");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*level*");
        mock.Verify(s => s.SetVolumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Audio_set_rejects_level_out_of_range(int level)
    {
        var mock = new Mock<IAudioService>();
        var tools = MakeTools(audio: mock.Object);

        var act = () => tools.Audio("set", level: level);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*level*");
        mock.Verify(s => s.SetVolumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Audio_get_set_mute_unmute_dispatch()
    {
        var mock = new Mock<IAudioService>();
        mock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudioState(40, false));
        var tools = MakeTools(audio: mock.Object);

        (await tools.Audio("get")).Should().Contain("40");
        mock.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);

        (await tools.Audio("set", level: 55)).Should().Contain("55");
        mock.Verify(s => s.SetVolumeAsync(55, It.IsAny<CancellationToken>()), Times.Once);

        (await tools.Audio("mute")).Should().Be("muted");
        mock.Verify(s => s.SetMutedAsync(true, It.IsAny<CancellationToken>()), Times.Once);

        (await tools.Audio("unmute")).Should().Be("unmuted");
        mock.Verify(s => s.SetMutedAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Notification_dispatches()
    {
        var mock = new Mock<INotificationService>();
        var tools = MakeTools(notification: mock.Object);

        var result = await tools.Notification("Hello", "World");

        result.Should().Contain("notification");
        mock.Verify(s => s.ShowAsync("Hello", "World", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecurityAudit_serializes()
    {
        var mock = new Mock<ISecurityService>();
        mock.Setup(s => s.AuditAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityAuditDto(true, true, 2, "On"));
        var tools = MakeTools(security: mock.Object);

        var json = await tools.SecurityAudit();

        json.Should().Contain("true").And.Contain("On");
        mock.Verify(s => s.AuditAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reliability_serializes()
    {
        var mock = new Mock<IReliabilityService>();
        mock.Setup(s => s.GetAsync(50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReliabilityReport(
                new[] { new MinidumpInfo("MEMORY.DMP", 10, DateTime.UtcNow) },
                Array.Empty<ReliabilityRecord>(),
                null));
        var tools = MakeTools(reliability: mock.Object);

        var json = await tools.Reliability();

        json.Should().Contain("MEMORY.DMP");
        mock.Verify(s => s.GetAsync(50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DriverList_serializes()
    {
        var mock = new Mock<IDriverService>();
        mock.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new DriverInfo("Widget", "Contoso", "1.0", "2020", true, "oem1.inf") });
        var tools = MakeTools(drivers: mock.Object);

        var json = await tools.DriverList();

        json.Should().Contain("Widget").And.Contain("Contoso");
        mock.Verify(s => s.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WmiQuery_dispatches()
    {
        var mock = new Mock<IWmiService>();
        mock.Setup(s => s.QueryAsync("Win32_Process", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<object>());
        var tools = MakeTools(wmi: mock.Object);

        await tools.WmiQuery("Win32_Process");

        mock.Verify(s => s.QueryAsync("Win32_Process", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Env_get_redacts_secret_names()
    {
        var mock = new Mock<IEnvService>();
        mock.Setup(s => s.GetAsync("API_KEY", EnvironmentVariableTarget.Process, It.IsAny<CancellationToken>()))
            .ReturnsAsync("super-secret");
        var tools = MakeTools(env: mock.Object);

        var result = await tools.Env("get", name: "API_KEY");

        result.Should().Contain("***REDACTED***");
        result.Should().NotContain("super-secret");
    }

    [Fact]
    public async Task Env_include_secrets_without_confirm_throws()
    {
        var mock = new Mock<IEnvService>();
        var tools = MakeTools(env: mock.Object);

        var act = () => tools.Env("get", name: "API_KEY", include_secrets: true, confirm: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<EnvironmentVariableTarget>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Env_include_secrets_with_confirm_returns_raw()
    {
        var mock = new Mock<IEnvService>();
        mock.Setup(s => s.GetAsync("API_KEY", EnvironmentVariableTarget.Process, It.IsAny<CancellationToken>()))
            .ReturnsAsync("super-secret");
        var tools = MakeTools(env: mock.Object);

        var result = await tools.Env("get", name: "API_KEY", include_secrets: true, confirm: true);

        result.Should().Contain("super-secret");
    }

    [Fact]
    public async Task Env_unknown_action_throws()
    {
        var act = () => MakeTools().Env("explode", name: "X");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*action*");
    }

    [Fact]
    public async Task Env_unknown_scope_throws()
    {
        var act = () => MakeTools().Env("get", name: "X", scope: "Galaxy");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*scope*");
    }

    [Fact]
    public async Task SystemInfo_unknown_category_throws()
    {
        var act = () => MakeTools().SystemInfo("cpu");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*category*");
    }
}
