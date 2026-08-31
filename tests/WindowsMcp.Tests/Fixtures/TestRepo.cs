using FluentAssertions;

namespace WindowsMcp.Tests.Fixtures;

internal static class TestRepo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must run from inside the repo");
        return dir!.FullName;
    }

    public static string ServerProjectPath() =>
        Path.Combine(Root(), "src", "WindowsMcp", "WindowsMcp.csproj");
}
