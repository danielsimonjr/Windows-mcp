using System.Reflection;
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

    /// <summary>The configuration that built this test assembly, and therefore the server beside it.</summary>
    public static string BuildConfiguration() =>
        typeof(TestRepo).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
        ?? throw new InvalidOperationException("the test assembly carries no AssemblyConfigurationAttribute");

    /// <summary>
    /// The <c>dotnet run</c> arguments that start the server that is already built. With
    /// <c>--no-build</c>, <c>dotnet run</c> starts the build of the configuration it is given, and
    /// Debug when it is given none, so the configuration must be the test's: CI builds only Release.
    /// </summary>
    public static string[] ServerRunArguments() =>
    [
        "run",
        "--project", ServerProjectPath(),
        "--framework", "net9.0-windows10.0.19041.0",
        "--configuration", BuildConfiguration(),
        "--no-build",
        "--no-restore",
    ];
}
