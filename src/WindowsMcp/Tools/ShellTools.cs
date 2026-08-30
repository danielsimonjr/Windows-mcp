using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ShellTools
{
    private readonly IPowerShellService _ps;

    public ShellTools(IPowerShellService ps)
    {
        _ps = ps;
    }

    [McpServerTool, Description(
        "Execute a PowerShell command and return the result including stdout, stderr, and exit code. " +
        "Requires confirm:true. High-risk patterns (Invoke-Expression, Start-Process, disk wipe, " +
        "nested -EncodedCommand, download cradles) are blocked.")]
    public async Task<string> Powershell(
        [Description("PowerShell command or script to execute")] string command,
        [Description("Must be true to confirm execution")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for powershell");
        var result = await _ps.RunAsync(command, ct);
        return JsonSerializer.Serialize(result);
    }
}
