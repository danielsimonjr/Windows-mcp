# Windows-mcp

An MCP server for Windows desktop automation, written in C# on the official
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
SDK. See [Tool reference](#tool-reference) for the 69 tools.

## Build

```powershell
git clone https://github.com/Steven23456/Windows-mcp.git
cd Windows-mcp
.\build.ps1
```

Output: one file, `bundle/WindowsMcp.exe` (~77 MB self-contained — bundles the .NET and
ASP.NET Core runtimes plus the native SkiaSharp library; nothing to install on the target
machine). `build.ps1` is a repo-root shortcut that calls `scripts/build-release.ps1`, where
the publish flags live (either works from any directory). That script runs this publish:

```powershell
dotnet publish src/WindowsMcp -c Release -o bundle -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none
```

`IncludeNativeLibrariesForSelfExtract` is what makes it genuinely one file — without it
`libSkiaSharp.dll` and `aspnetcorev2_inprocess.dll` are left loose next to the exe. One
file survives every publish flag, `libSkiaSharp.pdb` (a native asset of the SkiaSharp
package); the script deletes it afterwards. `bundle/` is gitignored — binaries are never
committed.

Requires the .NET 10 SDK for building. End users only need Windows 10 1703+
(for per-monitor DPI awareness V2) and System PowerShell (always present on
Windows 7+ at `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`).

## Register with Claude Code (or any MCP host)

Point your MCP host at the published exe. With Claude Code:

```powershell
claude mcp add --transport stdio Windows-mcp -- C:\path\to\Windows-mcp\bundle\WindowsMcp.exe
```

or in a `.mcp.json`:

The `env` block is belt-and-braces: `Hosting/EnvironmentRepair` already repairs a host-stripped
`PATHEXT` at startup, so setting it here is no longer required.

```json
"Windows-mcp": {
  "type": "stdio",
  "command": "C:/Development/Windows-mcp/bundle/WindowsMcp.exe",
  "args": [],
  "env": {
    "PATHEXT": ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL"
  }
}
```

Reconnect (`/mcp`) or start a new session. Tools appear as `mcp__Windows-mcp__*`.

The `.mcp.json` at the repo root is the **plugin** manifest: its
`${CLAUDE_PLUGIN_ROOT}/bundle/WindowsMcp.exe` path only resolves when the repo is installed
as a Claude Code plugin **and** a locally built `bundle/WindowsMcp.exe` is present (`bundle/`
is gitignored, so a fresh clone has none). Opening this repo as a plain project shows that
server as disconnected. Register the exe explicitly as above instead.

## Run over HTTP/HTTPS (remote)

The same exe can listen on a TCP port instead of stdin/stdout — for example inside
an RDP session on a remote Windows box, driven by Claude Code on your own machine:

```powershell
# On the remote machine, inside the interactive (RDP) session:
$env:WINDOWSMCP_API_KEY = "<a long random secret>"
.\WindowsMcp.exe --transport http --port 8765 --cert-thumbprint <thumbprint>
```

| Option | Env fallback | Default | Meaning |
|---|---|---|---|
| `--transport stdio\|http` | `WINDOWSMCP_TRANSPORT` | `stdio` | `http` = Streamable HTTP at `/mcp` |
| `--port <n>` | `WINDOWSMCP_PORT` | `8765` | TCP port |
| `--bind <ip>` | `WINDOWSMCP_BIND` | `0.0.0.0` | Listen address; `127.0.0.1` = this machine only |
| `--cert-thumbprint <hex>` | `WINDOWSMCP_CERT_THUMBPRINT` | — | Certificate in `LocalMachine\My` or `CurrentUser\My`; makes the port **HTTPS only** |
| `--api-key <key>` | `WINDOWSMCP_API_KEY` | — | Bearer token (≥ 16 printable ASCII chars). **Required** unless `--bind` is loopback |
| `--screenshot-scale <0.1-1.0>` | `WINDOWSMCP_SCREENSHOT_SCALE` | `1.0` | Multiplies every `screenshot` call's own `scale`; also applies to **stdio** |
| `--max-tree-elements <n>` | `WINDOWSMCP_MAX_TREE_ELEMENTS` | `500` | Element budget for `snapshot`/`get_state`, and for `scrape(source:"dom")`'s page walk, when a call names none; also applies to **stdio** |
| `--flash <on\|off>` | `WINDOWSMCP_FLASH` | `on` | Orange glow around the captured area for ~3.5 s after every `screenshot` — the signal to a person at the machine; also applies to **stdio** |
| `--profile-snapshot <on\|off>` | `WINDOWSMCP_PROFILE_SNAPSHOT` | `off` | Per-stage timings on `snapshot`/`screenshot` results, also logged to stderr; also applies to **stdio** |
| `--screenshot-backend <auto\|gdi\|wgc>` | `WINDOWSMCP_SCREENSHOT_BACKEND` | `auto` | Which backend reads the screen when a `screenshot` call says `auto`: `wgc` = Windows.Graphics.Capture, `gdi` = the classic screen copy; also applies to **stdio** |
| `--control-indicator <on\|off>` | `WINDOWSMCP_CONTROL_INDICATOR` | `on` | Pointer ring and takeover banner while a tool drives the mouse or keyboard (see [When the AI drives the mouse](#when-the-ai-drives-the-mouse)); also applies to **stdio** |
| `--control-hotkey <combo>` | `WINDOWSMCP_CONTROL_HOTKEY` | `ctrl+alt+shift+backspace` | The combination a person presses to take over; ctrl, alt or win must be written out (shift alone is refused) plus one key; also applies to **stdio** |

`WindowsMcp.exe --help` prints the same options (it lists `--screenshot-scale`,
`--max-tree-elements`, `--flash`, `--profile-snapshot` and `--screenshot-backend` under a
"Capture options (both transports)" heading, and `--control-indicator` and `--control-hotkey`
under "Control indicator (both transports)", since none of them is HTTP-only). No arguments =
stdio, unchanged.

One tool behaves differently on this transport: `scrape(summarize: true)` asks *your* client's
model through MCP sampling, which needs a session for the answer to come back on, and the
Streamable HTTP transport is stateless by design (a fresh server per request). Over HTTP the
client is never asked — `scrape` returns the page text with a `Note` naming the transport. Run
the server over stdio when you want the summary.

**Security model.** Every tool — `powershell`, `file_write`, `registry_set`,
`process kill`, … — is reachable on that port. So the server refuses to start on a
non-loopback address without an API key, and warns when it serves plain HTTP
off-loopback (the key and all tool traffic would cross the network in the clear).
Use HTTPS, and put the port behind a VPN or a firewall rule scoped to your client's
IP. The bearer check is constant-time; there is no rate limiting, so keep the key long.

**Logs.** Both transports log to stderr. Over HTTP each entry starts with the local time, to
the millisecond, and its UTC offset — `2026-10-20 13:06:34.223 -04:00 info: …` (time separator
and calendar follow the machine's culture); over stdio the MCP host stamps the lines it
captures, so the server adds none. The plain-HTTP warning above is one of these entries
(`warn:`); a `Logging:LogLevel` filter that would hide it only turns it back into a raw
`Windows-mcp: WARNING: …` line. Standard `Logging:Console` configuration wins over the defaults
where set: `$env:Logging__Console__FormatterName = "json"` (which has no timestamp unless you
also set `Logging__Console__FormatterOptions__TimestampFormat`), or
`Logging__Console__FormatterOptions__UseUtcTimestamp`. Over HTTP the deprecated flat
`Logging:Console:Format`, `TimestampFormat`, `IncludeScopes`, `DisableColors` and
`UseUtcTimestamp` settings are ignored; use `FormatterName` and `FormatterOptions:*`.

**Setup on the remote machine**

1. **Certificate** (for HTTPS). A self-signed one is fine for a private box — create
   it in the *current user* store so its private key is readable without elevation:
   ```powershell
   $cert = New-SelfSignedCertificate -DnsName "<remote-host-name>" -CertStoreLocation Cert:\CurrentUser\My
   $cert.Thumbprint                                            # → --cert-thumbprint
   Export-Certificate -Cert $cert -FilePath windows-mcp.cer    # public part, for the client
   certutil -encode windows-mcp.cer windows-mcp.pem            # Node wants PEM
   ```
   A `LocalMachine\My` certificate made from an elevated prompt has a key ACL of
   SYSTEM + Administrators; the server's startup error says so if it can't open the key.
2. **Firewall**:
   `New-NetFirewallRule -DisplayName "Windows-mcp" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow`
   (add `-RemoteAddress <client-ip>` to scope it).
3. **Session**. The input, screenshot, window and UI-automation tools need the
   **interactive desktop**, so run the exe inside the logged-in RDP session — not as a
   service or a Session-0 scheduled task. Disconnecting RDP keeps the session alive but
   can leave it without a rendered desktop (black screenshots, failed input): stay
   connected, or hand the session to the physical console first with
   `tscon $env:SESSIONNAME /dest:console` (elevated).

**Client (Claude Code on your machine)**

```powershell
$env:NODE_EXTRA_CA_CERTS = "C:\path\to\windows-mcp.pem"   # trust the self-signed cert
claude mcp add --transport http windows-mcp-remote https://<remote-host-name>:8765/mcp `
    --header "Authorization: Bearer <the api key>"
```

or in `.mcp.json`:

```json
{
  "mcpServers": {
    "windows-mcp-remote": {
      "type": "http",
      "url": "https://<remote-host-name>:8765/mcp",
      "headers": { "Authorization": "Bearer <the api key>" }
    }
  }
}
```

The host name in the URL must match the certificate's `-DnsName`.

## When the AI drives the mouse

While a tool that moves the pointer, presses keys or changes focus is running (`click`, `drag`,
`hover`, `type`, `key`, `shortcut`, `scroll`, `multi_select`, `multi_edit`, `interact_element`,
`file_dialog`, `focus`, `switch_to_window`), a person at the machine sees a blue ring around the
pointer and a dark banner at the top of the pointer's monitor:

> AI is controlling mouse & keyboard · Ctrl+Alt+Shift+Backspace to take over

It stays up while any of those calls is running and fades 5 s after the last one ends. Pressing
the combination hides it at once, and the agent's next tool result — from any tool — ends with
one extra block, once:

> Note: the user pressed Ctrl+Alt+Shift+Backspace at 14:03:12 (4 s ago) and took over the mouse
> and keyboard; the screen may have changed since your last snapshot.

That is all a takeover does. **Nothing is ever refused, delayed or cancelled, and your own mouse
and keyboard are never blocked**: the indicator informs, it does not gate the agent the way
upstream's desktop-control layer does. Moving the mouse is not a takeover; only the combination
is. The combination is registered only while the indicator is showing, so other applications
have it the rest of the time; if another application already holds it, the banner leaves the hint
out and the server logs a warning.

The ring and banner are click-through, never take focus, are not listed by `window` or
`snapshot`, and never appear in a `screenshot` or `ocr` capture (on Windows 10 before 2004 they
are hidden for the moment of each capture instead). The indicator is on by default under both
transports. Turn it off with `--control-indicator off` (`WINDOWSMCP_CONTROL_INDICATOR=off`), or
choose another combination with `--control-hotkey <combo>` (`WINDOWSMCP_CONTROL_HOTKEY`), such as
`ctrl+alt+f12`. Like the other desktop tools it needs the interactive session: run as a service
or a Session-0 task, there is no desktop to draw on and it does nothing.

## Companion skill

The plugin also ships a `windows` skill (`windows-mcp:windows`, `/windows`) —
a playbook that steers Claude toward these tools over raw PowerShell, with
composed workflows for common tasks and safety rails for destructive
operations. See [`skills/windows/SKILL.md`](skills/windows/SKILL.md).

Clients that never load the skill (Claude Desktop, an HTTP client, any other MCP host) still get
its core: the server's `initialize` result carries `instructions` on both transports — the loop
(snapshot → act by id → confirm → re-snapshot), the calls that retire snapshot ids, reading
`Truncated` and error text, stopping on a takeover note, and which changes are and are not
gated by `confirm` (`Hosting/ServerInstructions.cs`, under 2 000 characters).

## Tool reference

69 tools, grouped:

| Category | Tools |
|---|---|
| Input | `click`, `drag`, `hover`, `type`, `key`, `shortcut`, `scroll`, `wait`, `multi_select`, `multi_edit`, `clipboard` |
| Screen | `screenshot`, `ocr` |
| Window | `window`, `switch_to_window`, `launch`, `focus`, `multi_monitor` |
| UI Automation | `snapshot`, `get_state`, `find_element`, `get_element`, `get_text`, `assert_element`, `interact_element`, `get_table`, `wait_for` |
| Process / Shell | `process`, `process_inspect`, `start_process`, `powershell` (with `background: true` for jobs, `timeout_seconds` to bound one call), `job`, `service`, `scheduled_task`, `event_log` |
| File | `file_search`, `file_manage`, `file_dialog`, `file_read`, `file_write`, `file_info`, `file_hash`, `file_streams`, `archive` |
| Disk | `disk_inspect`, `storage_health` |
| System | `system_info`, `audio`, `notification`, `security_audit`, `reliability`, `driver_list`, `wmi_query`, `env`, `power_action` |
| Security | `verify_signature`, `defender_status`, `cert_store` |
| Startup | `startup_report` |
| Network | `network`, `firewall` |
| Registry | `registry_get`, `registry_set`, `registry_delete` |
| Web | `scrape` (a URL, or the page open in the browser), `http_request` |
| Monitoring | `integrity` (file-integrity tripwire), `fs_changes` (NTFS USN journal), `watch` (live directory watch) |

`click`, `type`, `scroll` and `drag` take a target the same way: `x` and `y` in virtual-desktop
pixels, or an `el_N` `element_id` from `snapshot`/`find_element` whose centre they aim at (an
off-screen element, or one with no bounds, is refused with the reason instead of clicking a
meaningless point — bring an off-screen one into view with `interact_element`
`scroll_into_view`). Giving both, or half a coordinate pair, is an error. `scroll` with no target
scrolls under the current cursor and `drag`'s origin defaults to it; each response says which of
`point`/`element`/`cursor` was used and where. `click(clicks: 0)` hovers, `scroll(shift_wheel:
true)` holds Shift and uses the vertical wheel for `left`/`right`, and `drag(duration_ms, steps)`
presses, nudges past the system drag threshold, moves through interpolated points and releases —
which is what file managers, canvases and browser drag-and-drop need to see.
`type(text, element_id?, clear?, caret?, press_enter?)` does the whole field edit in one call:
click the target, `clear` (Ctrl+A, Backspace), place the caret (`start`/`end`), type, press
Enter. Newlines are typed as Enter and tabs as Tab; text of 200+ characters with no other control
characters goes through the clipboard as one paste and the previous clipboard text is put back
(the result's `method` says `keys` or `paste`).

`multi_select(targets_json, ctrl?)` and `multi_edit(entries_json)` do a whole batch in one call.
`targets_json` is a JSON array of `{x, y}` points or `{element_id}` objects (a JSON string holding
that array is accepted too); `multi_edit`'s entries add `text` (required) plus optional `clear`
and `press_enter`, and each entry clicks its target then types through the same path as `type`.
Every target is resolved before any input is sent, so an off-screen element refuses the whole
batch with nothing done, and `multi_select` holds Ctrl from before the first click until after the
last and always releases it. Neither is atomic: they stop at the first failure and return
`failedIndex` and `error` alongside the per-entry `results` produced so far.

Name a window by `Title` — matched exact, then substring, then fuzzy (score ≥ 70), so
`switch_to_window("notepad")` finds `Untitled - Notepad` — or by the `Hwnd` from
`window(action:"list")`, which wins over a title. `switch_to_window`/`focus` return
`{Window, MatchStrategy, Score, Restored, Strategy, Success}`: a minimised window is restored
first, then the tool climbs a `SetForegroundWindow` → `AttachThreadInput` → ALT-nudge ladder and
re-reads the foreground window after each rung, so `Success` is observed rather than assumed. A
title that matches nothing is an error listing the open windows.

`launch(app_name, wait_for_window?, timeout_ms?)` takes the name a person would say. A path, or
an executable name that exists, is started outright; anything else is resolved against an
in-process catalog of Start Menu shortcuts and packaged (Store/MSIX) apps — matched exact, then
by prefix, then fuzzy (score ≥ 70) — so `launch("calc")` opens Calculator and `launch("vs code")`
opens Visual Studio Code, with no PowerShell anywhere in the path. It then polls the window
inventory for up to `timeout_ms` (default 10 s) for a window of the launched process, or a new
window whose title matches, and returns `{MatchedName, Kind, Score, Strategy, Pid, Hwnd, Title,
WindowDetected}`. A wait that runs out is `WindowDetected: false` with the pid, not an error; a
name that matches nothing lists the five nearest apps with their scores, and an abbreviation
that fits two apps equally (`wt`: Windows Terminal, Windows Tools) is an error naming both,
with nothing launched.

`window(action: "move" | "resize" | "set_bounds", ...)` places a window: `move` needs `x`/`y`,
`resize` needs `width`/`height`, `set_bounds` all four. The target is a `title`/`hwnd` matched the
same way, or the foreground window when neither is given; a minimized or maximized window is
refused naming its state unless `restore_first: true`. The window is never raised or activated,
and the `After` bounds in `{Window, Before, After, MatchStrategy, Score, Restored}` are re-read
from the window rather than echoed back from the request.

`wait(seconds)` pauses in-process (more than 0, at most 60) — use it between an action and the
next `snapshot` instead of a PowerShell sleep. `wait_for(text, …, condition?, use_dom?)` is the
conditional wait: `element_exists` (default), `element_enabled`, `focused_element`, `text_exists`
(the text anywhere in a snapshot of the scope, `use_dom: true` reading a browser page instead of
the window chrome) or `active_window` (the foreground window's title, exact → substring → fuzzy
70+); the aliases `element`, `enabled`, `focused`, `text` and `window` are accepted. It always
returns `{Satisfied, Condition, ElapsedMs, Attempts, Detail, Element?}` — a timeout is
`Satisfied: false` carrying the last `Detail`, not an error and not the string `"null"`.
`timeout_ms` is 0–120000 and `interval_ms` 0–5000.
`start_process(command, args_json?, cwd?, use_shell_execute?)` passes a JSON array of arguments
verbatim (no quoting) and returns `{pid, executable, args, cwd}`. `multi_monitor` reports each
monitor's `WorkArea`, `Orientation` (0/90/180/270), `EffectiveDpi` and `Scale` alongside its
bounds and primary flag.

`process(action: "list")` rows carry `CpuPercent` beside `Pid`, `Name`, `Path` and `MemoryMb` —
two `TotalProcessorTime` readings 250 ms apart, normalised across **all** cores, so a process
saturating one of eight reads `12.5`, as Task Manager shows. `sort_by`
(`memory` default | `cpu` | `name` | `pid`) and `limit` (`0` = all) order and cap that plain
list; both are refused with `includeLineage`, `groupByRoot` or `orphans`, which have their own
shapes and no CPU column. `process(action: "kill", …, graceful?, grace_ms?)` asks first: it posts
`WM_CLOSE` to every visible top-level window of the pid, waits up to `grace_ms` (default 3000,
max 60000) and only then forces the process, so an editor can show its save prompt. A process
with no window is forced at once and says so; `graceful` cannot be combined with `tree`, and
`includeLineage` cannot be combined with `groupByRoot`. `grace_ms` applies **per process** on a
name kill; a process that answers just as the clock runs out is reported
`exitedGracefully: true, forced: false`, and cancelling the request during the wait leaves the
process asked to close but not killed. Kills by
pid or name return `{killed:[{pid, name, graceful, exitedGracefully, forced, waitedMs}]}` (the
tree kill keeps its text count).

Every file tool takes **plain absolute** paths — `file_read`, `file_write`, `file_manage`'s `src`
and `dst`, `file_search`'s `root`, `file_hash`, `file_info`, `file_streams` and `archive` refuse
a relative one naming the parameter, since the server's working directory is not the caller's,
and refuse the `\\?\` / `\\.\` device forms in every spelling, since they bypass the
normalisation the containment guards rely on. `file_read(path, max_bytes?, encoding?,
offset_lines?, limit_lines?)` returns plain text by default and a JSON window
`{path, totalLines, offset, returned, truncated, content}` when either window parameter is given
(`offset_lines` is 1-based, `limit_lines: 0` runs to the end) — the way to page a large log,
since `max_bytes` bounds the file, not the window. `file_write(…, append?, create_parents?)`
appends instead of replacing and creates a missing parent directory unless `create_parents:
false` refuses it. `file_manage(action, src, dst?, confirm?, overwrite?, recursive?, pattern?,
include_hidden?, max_entries?)`: `copy`/`move` refuse an existing destination unless
`overwrite: true`, which **replaces** it — the old destination is moved aside, the copy or move
runs, and only then is it removed; a failure or a cancel puts it back (best effort: if it cannot,
the error says so and names where the previous content is). A directory is copied as a tree
without descending into junctions or symlinks, a cross-volume move is a copy then a delete (links
are dropped, and the destination is complete before the source goes), a missing destination
parent is created, and a volume root, the same path, a destination inside the source or one that
contains it are refused before anything is touched — judged on the volume's own spelling of both
paths, so a `subst` letter or a junction cannot smuggle one inside the other. `delete` refuses a
non-empty directory unless `recursive: true`; deleting a junction removes the link only
(`removed link …`), and deleting what is not there answers `nothing at … to delete`. `list`
returns `{Entries:[{Path, Name, IsDirectory, Size, Modified, Hidden, IsLink}], Truncated,
MaxEntries}` — `pattern` is a case-insensitive name glob (a path separator in it is refused),
`recursive` descends but never into a junction or symlink, hidden or system entries are skipped
unless `include_hidden: true`, and the walk stops at `max_entries` (default 1000, range
1–100000) with `Truncated: true`.

`registry_get(hive, path)` without `value_name` returns the whole key —
`{Path, Values: [{Path, Name, Data, Kind}], SubKeys: [...]}`, an empty path listing the hive
root — instead of the value names joined with commas; with a `value_name` it is unchanged.
`registry_delete(hive, path, value_name?, recursive?, confirm)` removes that one value, or the
key itself when no `value_name` is given: a key with sub-keys also needs `recursive: true`, the
hive root and the profile/OS roots (`Software`, `Software\Microsoft`, `System`,
`SYSTEM\CurrentControlSet`, `Environment`, …) are refused outright, and deleting what is not
there is `existed: false` rather than an error. It returns
`{hive, path, valueName?, deleted, existed, subKeysRemoved?}`.

`notification(title, message, app_id?)` shows the toast in-process through WinRT — no PowerShell
— under an AppUserModelId. The default `Windows-MCP` is registered under
`HKCU\Software\Classes\AppUserModelId` by the server on first use; a packaged app's AUMID (the
`Package_hash!App` form) shows the toast under that app, and any other id must already be
registered or Windows drops the toast. Returns `{shown, appId, registered, note?}`, where a
dropped toast is `shown: false` with the reason in `note`.

`powershell(command, background?, timeout_seconds?)` passes the script whole. `timeout_seconds`
(1–900) bounds one call: on expiry the child tree is killed and the result comes back with
`TimedOut: true`, `ExitCode: -1` and whatever stdout the script wrote before it hung, rather than
an error. `0` (the default) leaves the 15-minute execution backstop as the only clock, and the
parameter cannot be combined with `background: true` — a job runs until it finishes or
`job(cancel)` stops it. Each foreground stream is capped at 1 000 000 characters with the most
recent tail kept, as a job's output already was; `StdoutTrimmedChars`/`StderrTrimmedChars` say how
much was dropped.

`scrape(url?, query?, source?, summarize?, max_chars?, window?)` returns
`{Source, Url, Title, Chars, Truncated, Content, Summarized, Model, Note}`. `source: "http"`
(the default) fetches `url` and converts the HTML to markdown, reporting the URL it ended up at
after any redirect; `source: "dom"` reads the page already open in a Chromium browser (Edge,
Chrome, Brave, Opera, Vivaldi — not Firefox, which exposes no page to UI Automation): the window
named by `window`, else the frontmost one, ending with a scroll hint saying whether there is more
of the page above or below. `Chars` is the size before `max_chars` (default 100 000, max
1 000 000) cut the text, and `Truncated` covers both that cut and a page walk the element budget
stopped early — `Note` then names `--max-tree-elements`. `summarize: true` hands the text to
*your* client's model through MCP sampling, with `query` as the question to answer from it, and
falls back to the plain text with a `Note` whenever the client cannot or will not answer.

Every tool also carries MCP annotations — a title plus `readOnlyHint`, `destructiveHint`,
`idempotentHint` and `openWorldHint` — so a client can auto-approve reads and confirm the
destructive ones without a per-tool allowlist.

## Safety rails

Destructive tools require `confirm: true` as an argument and throw
`ArgumentException` otherwise:

- `file_write`, `file_manage(action="delete")`
- `process(action="kill")`, `service(action="stop"|"restart")`,
  `scheduled_task(action="delete")`
- `registry_set`, `registry_delete`
- `power_action`
- `firewall(action="add"|"remove")`
- `env(action="set")`

Not every tool annotated destructive has the gate: `powershell`, `archive` (zip and unzip
overwrite what is at `dst`), `file_manage` copy/move with `overwrite: true`, `scheduled_task`
create and run, `service` start, `job` cancel, `window` close and `http_request` run without
`confirm`, and the server instructions tell every client so.

Beyond the confirm gate, the destructive file actions ask for the specific permission they need:
a copy or a move over an existing destination needs `overwrite: true`, and deleting a non-empty
directory needs `recursive: true`. Neither used to be asked for — both defaults now refuse rather
than destroy data the caller did not name. What they *do* ask for, they check before they touch
anything: a missing or locked source, a volume or share root at either end, and a destination
that is the source, inside it or containing it are all refused before the first byte moves. With
`overwrite: true` the existing destination is set aside rather than deleted, so a failure or a
cancel mid-copy puts it back; a junction or symlink is never descended into, never recreated
across volumes, and `delete` of one removes the link and leaves its target alone.

A refusal reaches you as its own sentence. The server surfaces the message of a deliberate answer
— a refused parameter combination, a guard abort, a key or window that is not there, and the file
system's own "not found" / "access denied" / "in use" / "path too long" with the path in it, and
what the target itself refused: a UI pattern a control lacks, a disabled control (refused before
anything is touched), an element whose window closed, a command that does not exist, a power
action Windows declined, a registry key the server may not open (named with its hive) —
instead of the SDK's generic `An error occurred invoking '<tool>'.`, capped at 2 000 characters.
Unexpected faults (a null reference, an unrecognised COM or Win32 failure) stay masked.

`env(get|list)` redacts values for variables whose name contains
`KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL/PRIVATE/PAT` (case-insensitive).
Pass `include_secrets: true` to opt out.

`scrape` and `http_request` fetch **http and https only** — any other scheme (`ftp:`, `file:`,
`data:`, `ws:`, `javascript:`) is refused by name before anything is resolved — and they reject
private IP ranges (RFC1918, link-local, loopback, IPv6 `fc00::/7` + `fe80::/10`) including via
DNS rebinding: public URLs only by default.

## Performance notes

On first launch, the single-file binary extracts native dependencies
(SkiaSharp, etc.) to `%TEMP%\.net\WindowsMcp\<hash>\`, adding ~3-5 sec
startup. Subsequent launches are warm.

If you hit the 30s Claude Code startup timeout, add a Defender exclusion
for the `bundle/` folder.

## Development

```powershell
dotnet build                                       # incremental
dotnet test --filter "Category=Unit"               # fast loop (mocked, seconds)
dotnet test --filter "Category=Integration"        # exercises real Windows APIs
dotnet test --filter "Category=UIAutomation"       # needs an interactive desktop (Notepad fixture, real input injection)
dotnet test                                        # full suite
```

Architecture docs live in [`docs/architecture/`](docs/architecture/) (OVERVIEW,
ARCHITECTURE, COMPONENTS, DATAFLOW). The feature backlog against the upstream
Python server is [`docs/upstream-parity-checklist.md`](docs/upstream-parity-checklist.md).

## License

MIT — see [LICENSE](LICENSE).
