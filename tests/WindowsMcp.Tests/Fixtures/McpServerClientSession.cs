using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Collections.Concurrent;

namespace WindowsMcp.Tests.Fixtures;

public sealed class McpServerClientSession : IAsyncDisposable
{
    private McpServerClientSession(McpClient client)
    {
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<McpServerClientSession> StartAsync(CancellationToken ct = default)
    {
        var stderrLines = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "Windows-mcp protocol tests",
                Command = "dotnet",
                WorkingDirectory = TestRepo.Root(),
                Arguments =
                [
                    "run",
                    "--project", TestRepo.ServerProjectPath(),
                    "--framework", "net9.0-windows10.0.19041.0",
                    "--no-build",
                    "--no-restore",
                ],
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = stderrLines.Enqueue,
            },
            NullLoggerFactory.Instance);

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ProtocolVersion = Mcp20Protocol.Version,
                    ClientInfo = new Implementation
                    {
                        Name = "WindowsMcp.Tests",
                        Version = "1.0.0",
                    },
                    Capabilities = new ClientCapabilities(),
                    InitializationTimeout = TimeSpan.FromSeconds(20),
                    DiscoverProbeTimeout = TimeSpan.FromSeconds(20),
                },
                NullLoggerFactory.Instance,
                ct);

            return new McpServerClientSession(client);
        }
        catch (Exception ex)
        {
            if (transport is IAsyncDisposable asyncTransport)
                await asyncTransport.DisposeAsync();

            throw new InvalidOperationException(
                $"failed to establish an MCP client session.{FormatStderr(stderrLines)}",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is IAsyncDisposable asyncClient)
            await asyncClient.DisposeAsync();
    }

    private static string FormatStderr(ConcurrentQueue<string> stderrLines)
    {
        return stderrLines.IsEmpty
            ? string.Empty
            : $"{Environment.NewLine}stderr:{Environment.NewLine}{string.Join(Environment.NewLine, stderrLines)}";
    }
}
