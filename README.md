# Windows-mcp

An MCP server for Windows desktop automation, written in C# on the official
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
SDK. **63 tools** across input, screen, window, UI automation, process/shell,
file, disk, system, security, startup, network, registry, and web categories.

> **History:** Versions 0.x through 0.8.5 were written in Python. v0.2.0 (2026-05-26)
> is a complete C# rewrite — see [CHANGELOG.md](CHANGELOG.md) for the migration
> notes. The Python source tree is preserved in
> `legacy/python-pre-csharp-conversion-archive-2026-05-26.zip`.

## Build

```powershell
git clone https://github.com/danielsimonjr/Windows-mcp.git
cd Windows-mcp
dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true
```

Output: `dist/WindowsMcp.exe` (~56 MB self-contained; no .NET runtime required
on the target machine).

Requires the .NET 9 SDK for building. End users only need Windows 10 1703+
(for per-monitor DPI awareness V2) and System PowerShell (always present on
Windows 7+ at `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`).

## Register with Claude Code (or any MCP host)

Add to your MCP host config (e.g.,
`~/.claude/local-marketplace/mcp-host/.mcp.json`):

```json
{
  "mcpServers": {
    "Windows-mcp": {
      "type": "stdio",
      "command": "C:/path/to/Windows-mcp/dist/WindowsMcp.exe",
      "args": []
    }
  }
}
```

Run `/reload-plugins`. Tools appear as `mcp__Windows-mcp__*`.

## MCP 2.0 support

This server's MCP surface is intentionally **tools-only over stdio**, implemented by
the official `ModelContextProtocol` **2.2.0** SDK and pinned to the
**2026-07-28** protocol revision in the conformance tests.

What is verified:

- server handshake and server info
- tools capability advertisement
- `tools/list` schema generation and complete-result shape
- `tools/call` success/error behavior and text content blocks
- JSON-RPC method-not-found handling

What this server intentionally does **not** expose:

- prompts
- resources / resource templates / subscriptions
- completions
- roots
- sampling / elicitation
- logging configuration
- HTTP transport

The repeatable smoke suite lives in
`tests/WindowsMcp.Tests/Protocol/Mcp20ProtocolTests.cs`
and runs as part of `dotnet test` on Windows.

## Companion skill

The plugin also ships a `windows` skill (`windows-mcp:windows`, `/windows`) —
a playbook that steers Claude toward these tools over raw PowerShell, with
composed workflows for common tasks and safety rails for destructive
operations. See [`skills/windows/SKILL.md`](skills/windows/SKILL.md).

## Tool reference

63 tools, grouped:

| Category | Tools |
|---|---|
| Input | `click`, `drag`, `hover`, `type`, `key`, `shortcut`, `scroll`, `clipboard` |
| Screen | `screenshot`, `ocr` |
| Window | `window`, `switch_to_window`, `launch`, `focus`, `multi_monitor` |
| UI Automation | `get_state`, `find_element`, `get_element`, `get_text`, `assert_element`, `interact_element`, `get_table`, `wait_for` |
| Process / Shell | `process`, `start_process`, `powershell`, `service`, `scheduled_task`, `event_log` |
| File | `file_search`, `file_manage`, `file_dialog`, `file_read`, `file_write`, `file_info`, `file_hash`, `file_streams`, `archive` |
| Disk | `disk_inspect`, `storage_health` |
| System | `system_info`, `audio`, `notification`, `security_audit`, `reliability`, `driver_list`, `wmi_query`, `env`, `power_action` |
| Security | `verify_signature`, `defender_status`, `cert_store` |
| Startup | `startup_report` |
| Network | `network`, `firewall` |
| Registry | `registry_get`, `registry_set` |
| Web | `scrape`, `http_request` |

## Safety rails

Destructive tools require `confirm: true` as an argument and throw
`ArgumentException` otherwise:

- `file_write`, `file_manage(action="delete")`
- `process(action="kill")`, `service(action="stop"|"restart")`,
  `scheduled_task(action="delete")`
- `registry_set`
- `power_action`
- `firewall(action="add"|"remove")`
- `env(action="set")`

`env(get|list)` redacts values for variables whose name contains
`KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL/PRIVATE/PAT` (case-insensitive).
Pass `include_secrets: true` to opt out.

`scrape` and `http_request` reject private IP ranges (RFC1918, link-local,
loopback, IPv6 `fc00::/7` + `fe80::/10`) including via DNS rebinding —
public URLs only by default.

## Performance notes

On first launch, the single-file binary extracts native dependencies
(SkiaSharp, etc.) to `%TEMP%\.net\WindowsMcp\<hash>\`, adding ~3-5 sec
startup. Subsequent launches are warm.

If you hit the 30s Claude Code startup timeout, add a Defender exclusion
for the `dist/` folder.

## Development

```powershell
dotnet build                                       # incremental
dotnet test --filter "Category=Unit"               # fast loop (29 tests, ~1s)
dotnet test --filter "Category=Integration"        # exercises real Windows APIs
dotnet test --filter "Protocol=Mcp20"              # MCP 2.0 conformance smoke suite
dotnet test --filter "Category=UIAutomation"       # launches Notepad fixture
dotnet test                                        # full suite
```

See `docs/superpowers/specs/2026-05-24-windows-mcp-csharp-conversion-design.md`
for the architecture spec and
`docs/superpowers/plans/2026-05-24-windows-mcp-csharp-conversion.md` for the
22-task implementation plan that produced this version.

## License

MIT — see [LICENSE](LICENSE).
