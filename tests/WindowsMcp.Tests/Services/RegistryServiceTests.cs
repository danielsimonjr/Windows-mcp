using FluentAssertions;
using Microsoft.Win32;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class RegistryServiceTests : IDisposable
{
    private readonly string _ns = $"Software\\WindowsMcp.Tests\\{Guid.NewGuid():N}";
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_ns); } catch { }
    }

    [Fact]
    public async Task Set_then_Get_roundtrips_string_value()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "TestVal", "hello", "String");
        var v = await svc.GetAsync("HKCU", _ns, "TestVal");
        v.Data.Should().Be("hello");
    }

    [Fact]
    public async Task Get_throws_KeyNotFound_for_missing_path()
    {
        var svc = new RegistryService();
        Func<Task> act = () => svc.GetAsync("HKCU", "Software\\DoesNotExistXYZ123", null);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task EnumerateValues_returns_all_values_with_kinds()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Alpha", "one", "String");
        await svc.SetAsync("HKCU", _ns, "Beta", 42, "DWord");

        var vals = await svc.EnumerateValuesAsync("HKCU", _ns);

        vals.Select(v => v.Name).Should().BeEquivalentTo(new[] { "Alpha", "Beta" });
        vals.Single(v => v.Name == "Beta").Kind.Should().Be("DWord");
    }

    [Fact]
    public async Task EnumerateValues_reads_binary_data_as_byte_array()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "Bin", new byte[] { 3, 0, 0, 0 }, "Binary");

        var vals = await svc.EnumerateValuesAsync("HKCU", _ns);

        vals.Single(v => v.Name == "Bin").Data.Should().BeOfType<byte[]>()
            .Which.Should().Equal((byte)3, (byte)0, (byte)0, (byte)0);
    }

    [Fact]
    public async Task EnumerateValues_returns_empty_for_missing_key()
    {
        var svc = new RegistryService();
        var vals = await svc.EnumerateValuesAsync("HKCU", "Software\\DoesNotExistXYZ123");
        vals.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateSubKeys_returns_child_key_names()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns + "\\Child1", "x", "1", "String");
        await svc.SetAsync("HKCU", _ns + "\\Child2", "x", "1", "String");

        var subs = await svc.EnumerateSubKeysAsync("HKCU", _ns);

        subs.Should().Contain(new[] { "Child1", "Child2" });
    }

    [Fact]
    public async Task EnumerateSubKeys_returns_empty_for_missing_key()
    {
        var svc = new RegistryService();
        var subs = await svc.EnumerateSubKeysAsync("HKCU", "Software\\DoesNotExistXYZ123");
        subs.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumerateSubKeys_for_empty_path_lists_the_hive_root()
    {
        // The startup report enumerates HKU\<SID> via an empty root path; ensure that works
        // (and does not throw from disposing the predefined base key).
        var svc = new RegistryService();
        var subs = await svc.EnumerateSubKeysAsync("HKCU", "");
        // Registry key names are case-insensitive; the returned casing varies by
        // environment (e.g. "Software" on a typical desktop vs "SOFTWARE" on hosted
        // CI runners), so compare case-insensitively rather than by exact casing.
        subs.Should().Contain(k => string.Equals(k, "Software", StringComparison.OrdinalIgnoreCase));
    }
}
