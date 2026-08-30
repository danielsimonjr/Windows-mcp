using System.Diagnostics;
using System.Text.Json.Nodes;

namespace WindowsMcp.Tests.Fixtures;

public sealed class RawMcpServerProcess : IAsyncDisposable
{
    private readonly Process _process;

    private RawMcpServerProcess(Process process)
    {
        _process = process;
        _process.StandardInput.AutoFlush = true;
    }

    public static async Task<RawMcpServerProcess> StartAsync()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestRepo.Root(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(TestRepo.ServerProjectPath());
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add("net9.0-windows10.0.19041.0");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start Windows-mcp test server process");

        var server = new RawMcpServerProcess(process);
        await server.InitializeAsync();
        return server;
    }

    public async Task<JsonObject> SendRequestAsync(JsonObject request, TimeSpan? timeout = null)
    {
        await _process.StandardInput.WriteLineAsync(request.ToJsonString());

        var responseTask = _process.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(responseTask, Task.Delay(timeout ?? TimeSpan.FromSeconds(20)));
        if (completed != responseTask)
            throw new TimeoutException("timed out waiting for a JSON-RPC response from the Windows-mcp test server");

        var line = await responseTask;
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidOperationException("the Windows-mcp test server returned an empty stdout line");

        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"stdout was not a JSON object: {line}");
    }

    private async Task InitializeAsync()
    {
        var response = await SendRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = Mcp20Protocol.Version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "WindowsMcp.Tests",
                    ["version"] = "1.0.0",
                },
            },
        });

        if (response["result"] is null)
            throw new InvalidOperationException($"initialize failed: {response.ToJsonString()}");

        await _process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // Best effort cleanup for a test child process.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
