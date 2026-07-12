using System.Text.Json;
using FluentAssertions;
using WindowsMcp;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// The version the MCP server reports about itself was a hardcoded literal in Program.cs and
/// silently rotted for three releases — it still said "0.4.1" while 0.5.0 and 0.6.0 shipped.
/// That matters more than cosmetics: this plugin is served from a per-version cache clone of the
/// committed bundle, so a stale bundle is otherwise invisible, and `serverInfo.version` is the
/// natural thing to check. It has to be true.
/// </summary>
[Trait("Category", "Unit")]
public class ServerInfoTests
{
    static string RepoRoot()
    {
        // Walk up from the test binary until the repo marker is found — no fragile ../../.. count.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from inside the repo to locate plugin.json");
        return dir!.FullName;
    }

    [Fact]
    public void ServerVersion_is_not_the_old_hardcoded_literal()
    {
        Program.ServerVersion.Should().NotBe("0.4.1",
            "the version must come from <Version> in Directory.Build.props, never a literal");
        Program.ServerVersion.Should().NotBe("0.0.0");
    }

    [Fact]
    public void ServerVersion_matches_the_plugin_manifest()
    {
        var manifest = Path.Combine(RepoRoot(), ".claude-plugin", "plugin.json");
        File.Exists(manifest).Should().BeTrue($"expected the plugin manifest at {manifest}");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var declared = doc.RootElement.GetProperty("version").GetString();

        Program.ServerVersion.Should().Be(declared,
            "the server must report the same version the plugin manifest advertises — if these "
            + "drift, bump <Version> in Directory.Build.props to match .claude-plugin/plugin.json");
    }
}
