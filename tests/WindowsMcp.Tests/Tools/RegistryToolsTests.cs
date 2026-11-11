using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class RegistryToolsTests
{
    [Fact]
    public async Task RegistryGet_dispatches_to_service()
    {
        var dto = new RegistryValueDto(@"HKCU\Software\Test", "MyValue", "hello", "String");
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.GetAsync("HKCU", @"Software\Test", "MyValue", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var tools = new RegistryTools(mock.Object);
        var result = await tools.RegistryGet("HKCU", @"Software\Test", "MyValue");

        result.Should().Contain("hello");
        mock.VerifyAll();
    }

    [Fact]
    public async Task RegistrySet_requires_confirm()
    {
        var mock = new Mock<IRegistryService>();
        var tools = new RegistryTools(mock.Object);

        Func<Task> act = () => tools.RegistrySet("HKCU", @"Software\Test", "MyValue", "data", "String", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.SetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- C-2: the read shape ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Insensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>C-2 (R1): no value name = the whole key, values and sub-keys, in one call.</summary>
    [Fact]
    public async Task RegistryGet_without_a_value_name_returns_the_values_and_the_sub_keys()
    {
        var key = new RegistryKeyDto(
            @"Software\Test",
            [new RegistryValueDto(@"Software\Test", "Alpha", "one", "String"),
             new RegistryValueDto(@"Software\Test", "Beta", 42, "DWord")],
            ["Child1", "Child2"]);
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.ListAsync("HKCU", @"Software\Test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(key);

        var json = await new RegistryTools(mock.Object).RegistryGet("HKCU", @"Software\Test");

        mock.Verify(s => s.ListAsync("HKCU", @"Software\Test", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "the single-value read is what a value_name asks for, not what an omitted one does");

        var dto = JsonSerializer.Deserialize<RegistryKeyDto>(json, Insensitive);
        dto.Should().NotBeNull();
        dto!.Path.Should().Be(@"Software\Test");
        dto.Values.Select(v => v.Name).Should().BeEquivalentTo(new[] { "Alpha", "Beta" });
        dto.Values.Single(v => v.Name == "Beta").Kind.Should().Be("DWord");
        dto.SubKeys.Should().BeEquivalentTo(new[] { "Child1", "Child2" },
            "the comma-joined value-name string carried no sub-keys at all");
    }

    /// <summary>C-2 (R1): the value_name path is unchanged - the new read must not swallow it.</summary>
    [Fact]
    public async Task RegistryGet_with_a_value_name_still_reads_the_single_value()
    {
        var dto = new RegistryValueDto(@"HKCU\Software\Test", "MyValue", "hello", "String");
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.GetAsync("HKCU", @"Software\Test", "MyValue", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var result = await new RegistryTools(mock.Object).RegistryGet("HKCU", @"Software\Test", "MyValue");

        result.Should().Contain("hello");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- C-2: registry_delete -----------------------------------------------------------------

    private static Mock<IRegistryService> NoDeletes()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mock.Setup(s => s.DeleteKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryKeyDeleteResult(true, 0));
        return mock;
    }

    private static void VerifyNothingDeleted(Mock<IRegistryService> mock)
    {
        mock.Verify(s => s.DeleteValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.DeleteKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Absent, or present and null - either is "this result does not carry that field".</summary>
    private static void ShouldNotCarry(JsonElement json, string name) =>
        (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("{0} belongs to the other branch of the delete", name);

    /// <summary>C-2 (R3): the first refusal, before anything is looked at.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("MyValue")]
    public async Task RegistryDelete_requires_confirm(string? valueName)
    {
        var mock = NoDeletes();
        var tools = new RegistryTools(mock.Object);

        Func<Task> act = () => tools.RegistryDelete("HKCU", @"Software\MyApp", valueName, recursive: false, confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        VerifyNothingDeleted(mock);
    }

    /// <summary>
    /// C-2 (R3): the guard runs in the tool, before the service. Moq proves nothing was called -
    /// a refusal that arrived after DeleteKeyAsync would be no guard at all.
    /// </summary>
    [Theory]
    [InlineData("Software", "Software")]
    [InlineData(@"SYSTEM\CurrentControlSet", "CurrentControlSet")]
    [InlineData("software/microsoft/windows", "windows")]
    [InlineData("", "")]
    public async Task RegistryDelete_refuses_a_guarded_root_without_touching_the_service(string path, string named)
    {
        var mock = NoDeletes();
        var tools = new RegistryTools(mock.Object);

        Func<Task> act = () => tools.RegistryDelete("HKCU", path, value_name: null, recursive: true, confirm: true);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        if (named.Length > 0)
            thrown.WithMessage($"*{named}*");
        VerifyNothingDeleted(mock);
    }

    /// <summary>C-2: the guard is about keys. Removing one value under Software is ordinary work.</summary>
    [Fact]
    public async Task RegistryDelete_of_a_value_under_a_guarded_root_is_allowed()
    {
        var mock = NoDeletes();

        var json = Parse(await new RegistryTools(mock.Object)
            .RegistryDelete("HKCU", "Software", value_name: "MyValue", recursive: false, confirm: true));

        json.GetProperty("deleted").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.DeleteValueAsync("HKCU", "Software", "MyValue", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>C-2 (R3): a value delete forwards to DeleteValueAsync and reports the value name.</summary>
    [Fact]
    public async Task RegistryDelete_forwards_a_value_delete_and_reports_what_it_removed()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteValueAsync("HKCU", @"Software\MyApp", "MyValue", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var json = Parse(await new RegistryTools(mock.Object)
            .RegistryDelete("HKCU", @"Software\MyApp", value_name: "MyValue", recursive: false, confirm: true));

        json.GetProperty("hive").GetString().Should().Be("HKCU");
        json.GetProperty("path").GetString().Should().Be(@"Software\MyApp");
        json.GetProperty("valueName").GetString().Should().Be("MyValue");
        json.GetProperty("existed").GetBoolean().Should().BeTrue();
        json.GetProperty("deleted").GetBoolean().Should().BeTrue();
        ShouldNotCarry(json, "subKeysRemoved");
        mock.Verify(s => s.DeleteValueAsync("HKCU", @"Software\MyApp", "MyValue", It.IsAny<CancellationToken>()),
            Times.Once);
        mock.Verify(s => s.DeleteKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>C-2 (R3): deleting what is gone is a no-op that says so, not an error.</summary>
    [Fact]
    public async Task RegistryDelete_of_a_missing_value_reports_existed_false()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var json = Parse(await new RegistryTools(mock.Object)
            .RegistryDelete("HKCU", @"Software\MyApp", value_name: "Gone", recursive: false, confirm: true));

        json.GetProperty("existed").GetBoolean().Should().BeFalse();
        json.GetProperty("deleted").GetBoolean().Should().BeFalse(
            "nothing was removed, so the tool must not claim it deleted something");
    }

    /// <summary>C-2 (R3): a key delete forwards the recursive flag and reports the descendant count.</summary>
    [Fact]
    public async Task RegistryDelete_forwards_a_key_delete_with_the_recursive_flag()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteKeyAsync("HKCU", @"Software\MyApp", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryKeyDeleteResult(true, 3));

        var json = Parse(await new RegistryTools(mock.Object)
            .RegistryDelete("HKCU", @"Software\MyApp", value_name: null, recursive: true, confirm: true));

        json.GetProperty("hive").GetString().Should().Be("HKCU");
        json.GetProperty("path").GetString().Should().Be(@"Software\MyApp");
        json.GetProperty("existed").GetBoolean().Should().BeTrue();
        json.GetProperty("deleted").GetBoolean().Should().BeTrue();
        json.GetProperty("subKeysRemoved").GetInt32().Should().Be(3);
        ShouldNotCarry(json, "valueName");
        mock.Verify(s => s.DeleteKeyAsync("HKCU", @"Software\MyApp", true, It.IsAny<CancellationToken>()),
            Times.Once);
        mock.Verify(s => s.DeleteValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>C-2 (R3): the default is the safe one - a key delete does not recurse unasked.</summary>
    [Fact]
    public async Task RegistryDelete_defaults_recursive_to_false()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryKeyDeleteResult(true, 0));

        await new RegistryTools(mock.Object).RegistryDelete("HKCU", @"Software\MyApp", confirm: true);

        mock.Verify(s => s.DeleteKeyAsync("HKCU", @"Software\MyApp", false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// C-2 (R3): the service refuses a key that has sub-keys and the refusal reaches the caller
    /// naming the flag that would have allowed it - the tool must not swallow or reword it.
    /// </summary>
    [Fact]
    public async Task RegistryDelete_passes_the_services_recursive_refusal_through()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteKeyAsync("HKCU", @"Software\MyApp", false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                @"Key HKCU\Software\MyApp has 2 sub-keys; pass recursive: true to delete them"));

        Func<Task> act = () => new RegistryTools(mock.Object)
            .RegistryDelete("HKCU", @"Software\MyApp", value_name: null, recursive: false, confirm: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recursive*");
    }

    /// <summary>C-2 (R3): a key that was already gone.</summary>
    [Fact]
    public async Task RegistryDelete_of_a_missing_key_reports_existed_false()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.DeleteKeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryKeyDeleteResult(false, 0));

        var json = Parse(await new RegistryTools(mock.Object)
            .RegistryDelete("HKCU", @"Software\MyApp\Gone", value_name: null, recursive: true, confirm: true));

        json.GetProperty("existed").GetBoolean().Should().BeFalse();
        json.GetProperty("deleted").GetBoolean().Should().BeFalse();
        json.GetProperty("subKeysRemoved").GetInt32().Should().Be(0);
    }

    // ---- D-10 R25e: the description says what the caller now gets ----------------------------

    [Fact]
    public void RegistryDelete_description_describes_the_access_denied_refusal_not_a_pass_through()
    {
        // Since D-10 R14 access-denied reaches the caller as one refusal naming the key (and,
        // after R25a, saying when a tree delete was denied part-way). "The OS error is passed
        // through" was true of the masked SecurityException era and promises the wrong thing.
        var description = typeof(RegistryTools).GetMethod(nameof(RegistryTools.RegistryDelete))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        description.Should().NotContainEquivalentOf("passed through");
        description.Should().NotContainEquivalentOf("OS error");
        description.Should().ContainEquivalentOf("denied", "the caller is told what a refusal looks like");
        description.Should().ContainEquivalentOf("elevat", "and what the remedy is");
    }
}

/// <summary>
/// C-2 (R4/R5): the one <c>registry_delete</c> test that goes through the real
/// <see cref="RegistryService"/>. Every test above hands the tool a Moq that returns whatever the
/// test wants, so all of them would stay green if the service never touched the registry at all -
/// the failure mode the disk_inspect reclaimable bug shipped with. This one creates a real key
/// under HKCU, reads it back through the tool and deletes it through the tool.
/// </summary>
[Trait("Category", "Integration")]
public class RegistryToolsIntegrationTests : IDisposable
{
    private readonly string _ns = $@"Software\WindowsMcp.Tests\{Guid.NewGuid():N}";
    private readonly RegistryTools _tools = new(new RegistryService());

    public void Dispose()
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_ns); } catch { }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Registry_get_then_delete_walks_a_real_key_and_removes_it()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Alpha", "one", "String");
        await svc.SetAsync("HKCU", _ns + @"\Child", "x", "1", "String");

        var listed = Parse(await _tools.RegistryGet("HKCU", _ns));
        listed.GetProperty("Values").EnumerateArray().Select(v => v.GetProperty("Name").GetString())
            .Should().Contain("Alpha", "the tool's read shape carries the real key's values");
        listed.GetProperty("SubKeys").EnumerateArray().Select(k => k.GetString())
            .Should().Contain("Child");

        var valueGone = Parse(await _tools.RegistryDelete("HKCU", _ns, value_name: "Alpha", confirm: true));
        valueGone.GetProperty("existed").GetBoolean().Should().BeTrue();
        (await svc.ListAsync("HKCU", _ns)).Values.Should().BeEmpty("the value really went");

        var refused = () => _tools.RegistryDelete("HKCU", _ns, value_name: null, recursive: false, confirm: true);
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recursive*");
        (await svc.ListAsync("HKCU", _ns)).SubKeys.Should().Contain("Child",
            "a refused delete leaves the tree alone");

        var keyGone = Parse(await _tools.RegistryDelete("HKCU", _ns, value_name: null, recursive: true, confirm: true));
        keyGone.GetProperty("existed").GetBoolean().Should().BeTrue();
        keyGone.GetProperty("subKeysRemoved").GetInt32().Should().Be(1);
        await ((Func<Task>)(() => svc.ListAsync("HKCU", _ns)))
            .Should().ThrowAsync<KeyNotFoundException>("the key itself is gone");

        var again = Parse(await _tools.RegistryDelete("HKCU", _ns, value_name: null, recursive: true, confirm: true));
        again.GetProperty("existed").GetBoolean().Should().BeFalse("deleting what is gone is a no-op");
        again.GetProperty("deleted").GetBoolean().Should().BeFalse();
    }
}
