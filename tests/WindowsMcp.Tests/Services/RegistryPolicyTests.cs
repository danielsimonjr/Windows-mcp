using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class RegistryPolicyTests
{
    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\foo")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows\AppInit_DLLs")]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\Foo")]
    [InlineData("HKLM", @"SOFTWARE\Policies\Microsoft\Windows")]
    public void ThrowIfSensitiveWrite_blocks_persistence_and_hijack_keys(string hive, string path)
    {
        var act = () => RegistryPolicy.ThrowIfSensitiveWrite(hive, path);
        act.Should().Throw<ArgumentException>().WithMessage("*blocked*");
    }

    [Fact]
    public void ThrowIfSensitiveWrite_allows_ordinary_hkcu_software_key()
    {
        var act = () => RegistryPolicy.ThrowIfSensitiveWrite("HKCU", @"Software\WindowsMcp.Tests");
        act.Should().NotThrow();
    }
}
