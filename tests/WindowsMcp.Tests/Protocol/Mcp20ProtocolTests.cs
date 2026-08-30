using System.Text.Json.Nodes;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Protocol;

[Trait("Category", "Integration")]
[Trait("Protocol", "Mcp20")]
public class Mcp20ProtocolTests
{
    [Fact]
    public async Task Handshake_reports_server_info_and_tools_only_capability_surface()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = await McpServerClientSession.StartAsync();

        session.Client.ServerInfo.Name.Should().Be("Windows-mcp");
        session.Client.ServerInfo.Version.Should().Be(Program.ServerVersion);
        session.Client.ServerCapabilities.Tools.Should().NotBeNull();
        session.Client.ServerCapabilities.Prompts.Should().BeNull();
        session.Client.ServerCapabilities.Resources.Should().BeNull();
        session.Client.ServerCapabilities.Completions.Should().BeNull();

        var ping = await session.Client.PingAsync();
        ping.Should().NotBeNull();
    }

    [Fact]
    public async Task ListTools_returns_complete_result_and_expected_schema()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = await McpServerClientSession.StartAsync();

        var result = await session.Client.ListToolsAsync(new ListToolsRequestParams());

        result.ResultType.Should().Be("complete");
        result.Tools.Should().HaveCount(63);

        var fileRead = result.Tools.Should().ContainSingle(t => t.Name == "file_read").Subject;
        fileRead.Description.Should().NotBeNullOrWhiteSpace();
        fileRead.InputSchema.GetProperty("type").GetString().Should().Be("object");
        fileRead.InputSchema.GetProperty("properties").TryGetProperty("path", out var pathProperty).Should().BeTrue();
        pathProperty.GetProperty("type").GetString().Should().Be("string");
        Required(fileRead).Should().Contain("path");
        Required(fileRead).Should().NotContain("max_bytes");
        Required(fileRead).Should().NotContain("encoding");
        fileRead.InputSchema.GetProperty("properties").TryGetProperty("ct", out _).Should().BeFalse();
        fileRead.InputSchema.GetProperty("properties").TryGetProperty("cancellationToken", out _).Should().BeFalse();

        var fileWrite = result.Tools.Should().ContainSingle(t => t.Name == "file_write").Subject;
        fileWrite.InputSchema.GetProperty("properties").TryGetProperty("confirm", out var confirmProperty).Should().BeTrue();
        confirmProperty.GetProperty("type").GetString().Should().Be("boolean");
        Required(fileWrite).Should().Contain("path");
        Required(fileWrite).Should().Contain("content");
        Required(fileWrite).Should().NotContain("confirm");
    }

    [Fact]
    public async Task Tool_calls_round_trip_text_content_blocks()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = await McpServerClientSession.StartAsync();

        var path = Path.Combine(Path.GetTempPath(), $"windows-mcp-mcp20-{Guid.NewGuid():N}.txt");

        try
        {
            var write = await session.Client.CallToolAsync(
                "file_write",
                new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["content"] = "hello from mcp20",
                    ["confirm"] = true,
                });

            write.IsError.Should().NotBeTrue();
            write.ResultType.Should().Be("complete");
            OnlyText(write).Should().Contain("wrote");

            var read = await session.Client.CallToolAsync(
                "file_read",
                new Dictionary<string, object?>
                {
                    ["path"] = path,
                });

            read.IsError.Should().NotBeTrue();
            read.ResultType.Should().Be("complete");
            OnlyText(read).Should().Be("hello from mcp20");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Caller_facing_tool_refusals_return_iserror_with_original_message()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = await McpServerClientSession.StartAsync();

        var result = await session.Client.CallToolAsync(
            "file_write",
            new Dictionary<string, object?>
            {
                ["path"] = Path.Combine(Path.GetTempPath(), $"windows-mcp-no-confirm-{Guid.NewGuid():N}.txt"),
                ["content"] = "missing confirm",
            });

        result.IsError.Should().BeTrue();
        result.ResultType.Should().Be("complete");
        OnlyText(result).Should().Contain("confirm: true");
    }

    [Fact]
    public async Task Unknown_json_rpc_method_returns_method_not_found()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var process = await RawMcpServerProcess.StartAsync();

        var response = await process.SendRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "bogus/method",
        });

        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        response["id"]!.GetValue<int>().Should().Be(1);
        response["error"].Should().NotBeNull();
        response["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        response["error"]!["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    private static string OnlyText(CallToolResult result)
    {
        var text = result.Content.Should().ContainSingle().Subject.Should().BeOfType<TextContentBlock>().Subject;
        return text.Text;
    }

    private static string[] Required(Tool tool)
    {
        return tool.InputSchema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(static e => e.GetString() ?? string.Empty).ToArray()
            : [];
    }
}
