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

    public static async Task<McpServerClientSession> StartAsync(
        string protocolVersion = Mcp20Protocol.Version, CancellationToken ct = default)
    {
        var stderrLines = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "Windows-mcp protocol tests",
                Command = "dotnet",
                WorkingDirectory = TestRepo.Root(),
                Arguments = TestRepo.ServerRunArguments(),
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
                    ProtocolVersion = protocolVersion,
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
            throw new InvalidOperationException(
                $"failed to establish an MCP client session.{FormatStderr(stderrLines)}",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
    }

    private static string FormatStderr(ConcurrentQueue<string> stderrLines)
    {
        return stderrLines.IsEmpty
            ? string.Empty
            : $"{Environment.NewLine}stderr:{Environment.NewLine}{string.Join(Environment.NewLine, stderrLines)}";
    }
}
