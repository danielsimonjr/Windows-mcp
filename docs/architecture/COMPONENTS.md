# Windows-MCP Component Reference

## Component Overview

This document provides detailed documentation for each component in the Windows-MCP C# architecture, including tool classes, service interfaces, service implementations, and data models.

---

## Program.cs — Host and DI Entry Point

### Purpose
Parses the command line (`Hosting/ServerOptions`, with `WINDOWSMCP_*` env fallbacks) and starts the MCP server over stdio (default) or Streamable HTTP (`--transport http`, built by `Hosting/WindowsMcpHost.BuildHttpApp`). The service registrations and MCP wiring both transports share live in `Hosting/WindowsMcpHost.AddWindowsMcp`.

### Location
`src/WindowsMcp/Program.cs`

### Startup Sequence

```csharp
public static async Task<int> Main(string[] args)
{
    // 1. Repair a host-stripped environment (PATHEXT, ProgramData, Path, …) before anything
    //    spawns a child; repaired names are logged to stderr once. Host-set values are never
    //    overwritten, except PATHEXT and (C-6) a Path with no System32 entry, which the
    //    registry's machine then user Path is appended to — never reordered or trimmed — by the
    //    pure Hosting/PathMerge, with the stock four directories as the empty-registry fallback
    EnvironmentRepair.Apply();

    // 2. Process AppUserModelID (taskbar grouping; toasts name their own id since C-4)
    PInvoke.SetCurrentProcessExplicitAppUserModelID("org.windows-mcp.server");

    // 3. Per-Monitor DPI Awareness V2 — physical pixel coordinates on HiDPI
    PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((nint)(-4)));

    // 4. Command line / WINDOWSMCP_* env → ServerOptions (exit 2 + usage on a bad option; --help)
    var options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
    return options.IsHttp ? await RunHttpAsync(options) : await RunStdioAsync(args, options);
}

static async Task<int> RunStdioAsync(string[] args, ServerOptions options)
{
    // Force UTF-8 to prevent JSON-RPC response buffering on Windows (cp1252 default)
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding  = System.Text.Encoding.UTF8;

    var builder = Host.CreateApplicationBuilder(args);
    WindowsMcpHost.ConfigureStderrLogging(builder.Logging, http: false);
    builder.AddWindowsMcp(options)      // Hosting/WindowsMcpHost: all singletons + AddMcpServer(ServerInfo, ServerInstructions)
        .WithStdioServerTransport();    //   + call-tool filter (ToolErrors; S-11's indicator bracket
                                        //   and takeover note) + WithToolsFromAssembly()
    await builder.Build().RunAsync();
    return 0;
}

static async Task<int> RunHttpAsync(ServerOptions options)
{
    // Refuse off-loopback without an API key; resolve --cert-thumbprint via CertificateLocator.
    // BuildHttpApp calls ConfigureStderrLogging(http: true): stderr as in stdio, plus a local
    // "yyyy-MM-dd HH:mm:ss.fff zzz" stamp on every entry (defaults; Logging:Console config wins),
    // and logs the off-loopback plain-HTTP warning as a warn: entry once the app is built (a raw
    // "Windows-mcp: WARNING:" stderr line instead if Logging:LogLevel filters would hide it).
    var app = WindowsMcpHost.BuildHttpApp(options, cert);   // Kestrel Listen(bind, port[, UseHttps])
    await app.RunAsync();                                    //   + bearer gate + MapMcp("/mcp"), stateless
    return 0;
}
```

### Registered Services

| Interface | Implementation |
|-----------|---------------|
| `IFlashOverlay` | `FlashOverlay` (always registered; the screen tools gate on `ScreenshotOptions.Flash`) |
| `IControlIndicator` | `ControlIndicator` (S-11; always registered beside the `ControlIndicatorOptions` record from `--control-indicator` / `--control-hotkey`; nothing is created until the first driving call, and nothing ever when off) |
| `IInputService` | `InputService` |
| `IScreenshotService` | `ScreenshotService` |
| `IOcrService` | `OcrService` |
| `IClipboardService` | `ClipboardService` |
| `IAudioService` | `AudioService` |
| `IPowerShellService` | `PowerShellService` |
| `IUIAutomationService` | `UIAutomationService` |
| `IFileSystemService` | `FileSystemService` |
| `IRegistryService` | `RegistryService` |
| `IServiceControlService` | `ServiceControlService` |
| `IEventLogService` | `EventLogService` |
| `ITaskSchedulerService` | `TaskSchedulerService` |
| `IProcessService` | `ProcessService` |
| `IVirtualDesktopService` | `VirtualDesktopService` |
| `IAppCatalogService` | `AppCatalogService` (B-8: the `launch` catalog) |
| `IWindowService` | `WindowService` |
| `IWmiService` | `WmiService` |
| `IStorageService` | `StorageService` |
| `IDiskService` | `DiskService` |
| `ISecurityService` | `SecurityService` |
| `IFirewallService` | `FirewallService` |
| `ICertStoreService` | `CertStoreService` |
| `IReliabilityService` | `ReliabilityService` |
| `IDriverService` | `DriverService` |
| `IFileStreamService` | `FileStreamService` |
| `IEnvService` | `EnvService` |
| `IPowerService` | `PowerService` |
| `INotificationService` | `NotificationService` |
| `INetworkService` | `NetworkService` |
| `IWebService` | `WebService` |
| `IAuthenticodeInspector` | `AuthenticodeInspector` |
| `ILspEnumerator` | `LspEnumerator` |
| `IShortcutResolver` | `ShortcutResolver` |
| `IStartupReportService` | `StartupReportService` |
| `IIntegrityService` | `IntegrityService` |
| `IUsnService` | `UsnService` |
| `IWatchService` | `WatchService` |
| `IJobService` | `JobService` |

---

## Tool Classes

Tool classes are `[McpServerToolType]`-annotated, sealed, and stateless (except for injected service references). All tool methods are `async Task<string>` and return JSON-serialized results or plain strings — except `screenshot`, which returns `async Task<CallToolResult>` so it can carry an image content block. Every `[McpServerTool]` names all five annotation arguments explicitly (C-7) — `Title`, `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld` — even where the value equals the SDK default, so clients see a title and all four hints on every tool; the reviewed classification table is `docs/design/C-7-tool-annotations.md` and `ToolInventoryTests` pins each column.

---

### `InputTools` — 11 tools
`src/WindowsMcp/Tools/InputTools.cs`

**Injected:** `IInputService`, `IClipboardService`, `IUIAutomationService` (B-4: the `element_id` lookup behind `ResolveTargetAsync`)

Every target-taking verb — `click`, `type`, `scroll`, `drag` and B-7's two batch tools — shares one private `ResolveTargetAsync(x, y, element_id, allowCursor)` (roadmap C1): exactly one of (`x` **and** `y`) or `element_id` — both, half a pair, or (where no cursor fallback is allowed) neither is an `ArgumentException` naming the parameters in play; an id is read through `IUIAutomationService.GetElementAsync` and turned into a point by the pure `ElementTarget.CentreOf`, which refuses an off-screen or boundless element before any input is sent; `allowCursor` (used by `Scroll` and `Drag`'s origin) makes "nothing given" mean the live cursor. Each response reports the resolved point and whether it came from a `point`, an `element` or the `cursor`.

| Method | Signature | Description |
|--------|-----------|-------------|
| `Click` | `(int? x=null, int? y=null, string? element_id=null, string button="left", int clicks=1, string? modifiers=null)` | Click a point or an element's centre. `clicks:0` hovers (`HoverAsync`, nothing pressed); negative is refused. `modifiers` (E-3): `ctrl` (alias `control`), `shift`, `alt` joined with `+`, any case or order, duplicates collapse — parsed by `ParseModifiers` before the target is resolved, so anything else (`win` included) or a modifier with `clicks:0` is an `ArgumentException` with no input sent. With modifiers the pointer moves to the target first (`HoverAsync`, so a point off every monitor is refused before a key goes down), then `HoldingAsync` presses the keys in the order ctrl, shift, alt, sends one `ClickAsync` and releases them in reverse — on a failed click and on cancellation too (released on a non-cancellable token), including a key whose key-down threw; a failed release is retried once, and a second failure after a sent click is an `InvalidOperationException` saying the click was sent and naming the keys that may still be down. Returns `{action: click\|hover, x, y, button, clicks, elementId?, name?, modifiers}` (`modifiers` the canonical `ctrl+shift` form, or null) |
| `Drag` | `(int? from_x=null, int? from_y=null, int? to_x=null, int? to_y=null, string? element_id=null, string? from_element_id=null, string button="left", int duration_ms=300, int steps=20)` | Press at the origin (a point, an element, or the cursor when nothing is given), move through the nudge + interpolated points, release on the destination (`to_x`/`to_y` or `element_id` — one of them is required). `duration_ms` 0–10000, `steps` 2–200. Returns `{fromX, fromY, toX, toY, button, durationMs, steps, fromTarget, elementId?, name?}` |
| `Hover` | `(int x, int y, int duration_ms=0)` | Hover or move cursor |
| `Type` | `(string text, int? x=null, int? y=null, string? element_id=null, bool clear=false, string caret="idle", bool press_enter=false, int pace_ms=5)` | Click the target first when one is given, then run the `TypePlanner` plan: `clear` (Ctrl+A, Backspace), `caret` `idle\|start\|end` (Ctrl+Home / Ctrl+End), the text by keys or by one clipboard paste, `press_enter` last. Returns `{typed, method: keys\|paste, clipboardRestored?, x?, y?, elementId?, name?}` |
| `Key` | `(string key, int repeat=1, int hold_ms=0)` | Press one key: a character (`a`, `7`, `/`), `f1`–`f24`, or a name (enter, tab, esc, win, printscreen, …). E-3: `repeat` 1–100 presses it N times 5 ms apart (`RepeatPace`) and returns `pressed <key> N times`; `hold_ms` 0–10000 sends key-down, waits, key-up through the same `HoldingAsync` release guarantee and returns `held <key> for M ms` (held, not auto-repeated); the two cannot be combined. For either, the key is resolved by `ShortcutParser.ResolveKey` before any press and a modifier (every ctrl/shift/alt/win name, left/right variants included — `IsModifier`) is refused; a held key must need no implied modifier on the active layout. A repeat that fails after at least one press is an `InvalidOperationException` saying `pressed i of N times`. Plain `key(k)` is unchanged (`pressed <key>`) |
| `Shortcut` | `(string shortcut)` | Press a chord (`ctrl+c`, `ctrl+shift+s`, `win+r`); a single part such as `win` is a bare key press |
| `Scroll` | `(string direction, int amount=3, int? x=null, int? y=null, string? element_id=null, bool shift_wheel=false)` | Scroll the wheel at a point, an element's centre, or under the cursor when no target is given. `shift_wheel` (left/right only, refused for up/down) holds Shift and uses the vertical wheel. Returns `{direction, amount, x, y, target: point\|element\|cursor, shiftWheel, elementId?, name?}` |
| `Wait` | `(double seconds)` | Pause in-process for `seconds` — more than 0 and at most 60, fractions allowed; anything outside that range is an `ArgumentException` naming it, and the token cancels the delay. Returns `{"waited": seconds}`. Annotated `ReadOnly = true, Idempotent = true` |
| `MultiSelect` | `(string targets_json, bool ctrl=true)` | B-7: click every target of a JSON array of `{x,y}` or `{element_id}` objects (a JSON string holding that array is unwrapped once) parsed by the pure `BatchTargets.ParseTargets`. All targets go through `ResolveTargetAsync` **before** the first click, so an off-screen element refuses the whole batch with nothing done; with `ctrl` the modifier is pressed before the first click and released in a `finally` after the last. Clicks run in order and stop at the first failure. Returns `{count, ctrl, results:[{index, x, y, elementId?, name?, ok}], failedIndex?, error?}` |
| `MultiEdit` | `(string entries_json)` | B-7: per entry, click the target then type — the same JSON array plus `text` (required) and the optional `clear` / `press_enter` (`BatchTargets.ParseEntries`). Typing runs B-1's `TypeAsync(text, TypeOptions(clear, Idle, press_enter, 5))`, so `clear` and `press_enter` mean what they mean on `type`. Same resolve-everything-first rule and same stop-at-the-first-failure rule. Returns `{count, results:[{index, x, y, elementId?, name?, typed, method, ok}], failedIndex?, error?}` |
| `Clipboard` | `(string action, string? text=null)` | `get` or `set` clipboard text |

---

### `UIAutomationTools` — 9 tools
`src/WindowsMcp/Tools/UIAutomationTools.cs`

**Injected:** `IUIAutomationService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Snapshot` | `(string scope="desktop", string? window=null, bool include_tree=false, int max_elements=0, string format="text", bool use_dom=false)` | One call for the whole desktop: window list (z-order, topmost first), active window, cursor, every interactive element with its centre coordinates and an action hint (click/fill/toggle/select/slide/scroll), and the scrollable regions with their percentages. `scope`: desktop (default) / foreground / window (needs `window`, exact-then-substring). `format="text"` (default) is the compact render, `"json"` the serialised `SnapshotResult` (`include_tree` adds the element tree). `max_elements` caps the walk (0 = `--max-tree-elements`, default 500); a truncated result says so. Element ids (`el_N`) work with `click`/`interact_element`/`get_element` and are valid until the next snapshot (`screenshot` annotate, `wait_for`'s text/focused polls and a dom `scrape` take one too). `use_dom:true` (A-5 phase 1, Chromium) walks every browser window in scope from its web page — the `RootWebArea` document — instead of the whole window, so the address bar and tab strip are left out, and adds a `Pages` section: one entry per browser window with the page document's id, title, URL, vertical scroll percent and visible text in document order (a window with no page document, e.g. Firefox or a page still loading, is walked whole and its entry says so) |
| `GetState` | `()` | UI element tree of the foreground app, three levels deep and bounded by the element budget; the root carries `Truncated`/`ElementLimit` when the walk was cut short |
| `FindElement` | `(string text, string kind="any", string scope="foreground", string? window=null, bool include_offscreen=false)` | Find elements whose name contains `text`; kind: any/interactive/text/scrollable; scope: foreground (default) / window (needs `window`) / desktop; off-screen elements dropped unless `include_offscreen`; ≤20 matches, capped after filtering |
| `GetElement` | `(string element_id)` | Properties of a specific element by ID, answered under the id passed (D-11) |
| `GetText` | `(string element_id)` | Extract text content (faster than OCR) |
| `AssertElement` | `(string element_id, string state, string? expected=null)` | exists / enabled / checked / visible / focused / value (needs `expected`); returns `PASS` or `FAIL: <state> — observed <what was found>` |
| `InteractElement` | `(string element_id, string action, string? value=null)` | click / invoke / toggle / select / focus / type / expand / collapse / set_value / scroll_into_view (E-2) via UIA patterns, with a physical click or keyboard fallback for `click` / `type`; returns `InteractResult` JSON naming what fired and, for the E-2 verbs, what the control shows afterwards. The action (and whether it takes a `value`) is validated before the element is read; an unsupported pattern or a disabled control is an `InvalidOperationException` naming it (D-10) |
| `GetTable` | `(string element_id)` | Extract grid/table data via `GridPattern` |
| `WaitFor` | `(string text, int timeout_ms=10000, int interval_ms=500, string kind="any", string scope="foreground", string? window=null, bool include_offscreen=false, string condition="element_exists", bool use_dom=false)` | B-6: poll until the condition holds. `condition` is `element_exists` (default) / `element_enabled` — the `find_element` path with the same filters, the window re-resolved each poll — / `focused_element` / `text_exists` — a `snapshot` of the scope, `use_dom` reading the browser page (A-5) — / `active_window` — the window inventory only, matched exact → substring → fuzzy 70+, no element walk; aliases `element\|enabled\|focused\|text\|window`, anything else is an `ArgumentException` listing them. `timeout_ms` 0–120000 and `interval_ms` 0–5000 are validated in the tool; blank `text` is refused naming the condition. Always returns `WaitForResult` JSON — a timeout is `Satisfied:false` with the last `Detail` (or `every poll failed: …`), never an exception and never the string `null` |

---

### `WindowTools` — 5 tools
`src/WindowsMcp/Tools/WindowTools.cs`

**Injected:** `IWindowService`, `IVirtualDesktopService` (the `desktops` action)

| Method | Description |
|--------|-------------|
| `Window` | `list` every user-visible top-level window in z-order (`include_minimized` default true, `include_hidden` default false), each row carrying the `DesktopId` of the virtual desktop it is on / `active` the foreground one (`{"found":false}` when there is none) / `desktops` the virtual-desktop inventory as `{"current": …\|null, "all": [{Id, Name, Index, IsCurrent}]}`, one read for both halves / `minimize`, `maximize`, `restore`, `close` a window resolved by `WindowMatcher` — an explicit `hwnd` wins, else the `title` exact → substring → fuzzy; the action is validated first, then that one of `title`/`hwnd` is present. The result is `WindowAction(Action, Title /* the matched window's */, Success, MatchStrategy, Score, Hwnd)`; nothing matched is a `KeyNotFoundException` listing the open titles, not `Success:false`. B-9 adds `move` (needs `x`,`y`), `resize` (needs `width`,`height`) and `set_bounds` (all four) — the argument rule is checked in the tool before the service, the target is the same matcher or the foreground window when neither `title` nor `hwnd` is given, and the result is `WindowBoundsResult` |
| `SwitchToWindow` | `(string? title=null, long? hwnd=null)` — bring a window to the foreground: matched by `hwnd` or by `title` exact → substring → fuzzy (score ≥ 70), restored if minimised, then `ForegroundLadder` climbs `SetForegroundWindow` → `AttachThreadInput`+`BringWindowToTop` → ALT nudge, re-reading `GetForegroundWindow` after each rung. Returns `ForegroundResult` JSON; neither argument is an `ArgumentException` raised before the inventory is read |
| `Focus` | Alias of `SwitchToWindow` — same parameters, same `ForegroundResult` |
| `Launch` | `(string app_name, bool wait_for_window=true, int timeout_ms=10000)` — B-8: a path or an existing executable name is started outright (`Strategy: "path"`), anything else is resolved through `IAppCatalogService` (exact → prefix → fuzzy 70+) and started by AUMID (packaged) or by ShellExecute on its `.lnk` (shortcut). With `wait_for_window` the window inventory is polled up to `timeout_ms` (1–60000, validated in the tool) for a window of the launched pid, else a new window whose title matches. Returns `LaunchResult` JSON; a timeout is `WindowDetected:false`, not an error |
| `MultiMonitor` | Enumerate monitors: index, device name, bounds, primary flag, plus `WorkArea`, `Orientation`, `EffectiveDpi` and `Scale` |

---

### `FileTools` — 9 tools
`src/WindowsMcp/Tools/FileTools.cs`

**Injected:** `IFileSystemService`, `IInputService`, `IFileStreamService`

C-1: every path parameter of every file tool — `FileRead`, `FileWrite`, `FileManage` (`src`
**and** `dst`), `FileSearch` (`root`), and since round 4b the read-only four `FileHash`,
`FileInfo`, `FileStreams` and `Archive` (`src` **and** `dst`) — goes through `RequireAbsolute`,
which refuses a path that is not fully qualified (`Path.IsPathFullyQualified`; UNC passes) with
one `ArgumentException` naming the parameter, raised in the tool **before** the service is
called — a relative path would resolve against the server's working directory, which the caller
cannot see. The same check refuses the `\\?\` and `\\.\` device forms in every spelling (`/` is
normalised to `\` first, so `//?/`, `//./`, `\\?/` and `/\?\` all count): they bypass the path
normalisation the service's containment guards rely on, and a model has no reason to send them.

| Method | Description |
|--------|-------------|
| `FileRead` | Read a file as text (`max_bytes`, `encoding` auto/utf-8/utf-16/ascii). C-1: with `offset_lines` (1-based; 0 and 1 both mean the first line) and/or `limit_lines` (0 = to the end) it returns the JSON window `{path, totalLines, offset, returned, truncated, content}` cut by the pure `LineWindow`; with neither it is the plain text it always was, and `max_bytes` still bounds the file |
| `FileWrite` | Write text to a file (`confirm:true`). C-1: `append:true` adds to the end (the reply says `appended`) instead of replacing; a missing parent directory is created unless `create_parents:false` refuses it by name |
| `FileManage` | `copy` / `move` / `delete` (`confirm:true`) / `list`. C-1: `copy`/`move` refuse an existing `dst` unless `overwrite:true`, which **replaces** it through the aside/commit/restore path below (a directory `src` is copied as a tree, junctions and symlinks neither descended into nor recreated; a cross-volume move is a copy then a delete; a missing `dst` parent is created); `delete` refuses a non-empty directory unless `recursive:true`, replies `removed link '<path>' (its target is untouched)` for a reparse point and `nothing at '<path>' to delete` when there was nothing there; `list` returns `FileListing` — `pattern` is a case-insensitive name glob (a path separator in it is refused naming the parameter), `recursive` descends but never into a reparse point, hidden and system entries are skipped unless `include_hidden:true`, and the walk stops at `max_entries` (default 1000, range 1–100000 — the tool refuses `0`, which is not "all" here) with `Truncated:true` |
| `FileInfo` | Get file or directory metadata |
| `FileSearch` | Search for files by glob pattern |
| `FileHash` | Compute a file's SHA256/SHA1/MD5 hex digest |
| `FileStreams` | NTFS alternate data streams + reparse (symlink/junction) target |
| `FileDialog` | Interact with a native open/save dialog |
| `Archive` | `zip` a directory or `unzip` an archive |

---

### `SecurityTools` — 3 tools
`src/WindowsMcp/Tools/SecurityTools.cs`

**Injected:** `IAuthenticodeInspector`, `ISecurityService`, `ICertStoreService`

| Method | Description |
|--------|-------------|
| `VerifySignature` | Catalog-aware Authenticode trust verdict for a file |
| `DefenderStatus` | Microsoft Defender posture (real-time/tamper protection, signature age, last scans) |
| `CertStore` | Enumerate a certificate store; flags self-signed (rogue-root) and expired certs |

---

### `SystemTools` — 9 tools
`src/WindowsMcp/Tools/SystemTools.cs`

**Injected:** `IWmiService`, `IEnvService`, `IPowerService`, `INotificationService`, `IAudioService`, `ISecurityService`, `IReliabilityService`, `IDriverService`

| Method | Description |
|--------|-------------|
| `SystemInfo` | WMI system info by category (os/memory/disk/gpu/battery) |
| `Audio` | Get/set volume or mute/unmute |
| `Notification` | Show a Windows toast in-process through WinRT under `app_id` (default `Windows-MCP`, registered under HKCU on first use); returns `{shown, appId, registered, note?}` |
| `SecurityAudit` | Firewall/Defender/UAC/BitLocker posture snapshot |
| `Reliability` | Crash minidumps + recent reliability failure records |
| `DriverList` | Installed PnP drivers with version/date/signer/signed-state (BYOVD surface) |
| `WmiQuery` | Execute arbitrary WMI queries |
| `Env` | Get, set, or list environment variables (secret-name redaction) |
| `PowerAction` | Shutdown, reboot, logoff, lock, sleep, hibernate |

---

### `ProcessTools` — 6 tools
`src/WindowsMcp/Tools/ProcessTools.cs`

**Injected:** `IProcessService`, `IServiceControlService`, `ITaskSchedulerService`, `IEventLogService`

| Method | Description |
|--------|-------------|
| `Process` | actions `list\|orphans\|kill`. `list` is plain by default and carries `CpuPercent` (C-3: two `TotalProcessorTime` readings 250 ms apart, normalised across all cores), ordered and capped by `sort_by` (`memory` default\|`cpu`\|`name`\|`pid`) and `limit` (0 = all) — both **refused**, not ignored, with the shapes below; `includeLineage:true` adds recycle-aware parent lineage + signals (age, runtime kind, system-adjacency, root PID); `groupByRoot:true` collapses processes under their nearest-live root ancestor. `orphans` lists lineage rows whose parent is gone (recycle-aware). `kill` by PID or name (`confirm:true` required); `tree:true` kills descendants leaves-first; `startTime` guards against PID reuse; `graceful:true` posts `WM_CLOSE` to the pid's visible windows and waits `grace_ms` (default 3000, range 0–60000) before forcing, and is refused with `tree`. PID and name kills return `{killed:[{pid, name, graceful, exitedGracefully, forced, waitedMs}]}`; the tree kill keeps its text count. Data path: WMI bulk query → CIM datetime parse → pure lineage classifier. |
| `ProcessInspect` | Deep per-process detail: parent PID, command line, start time, loaded modules |
| `StartProcess` | `(string command, string? args_json=null, string? cwd=null, bool use_shell_execute=false)` — `command` alone keeps the old whole-command-line split; with `args_json` (a JSON array of strings, parsed by `ArgvJson`) it is the executable only and the items go to `ArgumentList` verbatim. A `cwd` that does not exist is a `DirectoryNotFoundException` raised before anything is spawned. Returns `{pid, executable, args, cwd}` |
| `Service` | List/status/start/stop/restart Windows services |
| `ScheduledTask` | List/get/run/create/delete scheduled tasks |
| `EventLog` | Query the Windows Event Log |

---

### `ScreenTools` — 2 tools
`src/WindowsMcp/Tools/ScreenTools.cs`

**Injected:** `IScreenshotService`, `IOcrService`, `IWindowService` (monitor inventory), `IInputService` (cursor position), `IUIAutomationService` (the element walk `annotate` draws and lists), `IFlashOverlay` (hidden before every capture, shown after when `--flash` is on), plus the `ScreenshotOptions` record (`--screenshot-scale`, `--flash`, `--profile-snapshot`, `--screenshot-backend`)

| Method | Description |
|--------|-------------|
| `Screenshot` | Capture the primary display, selected monitors (`display="all"`/`"0,2"`) or an `x,y,w,h` region (virtual-desktop pixels, validated against the virtual screen). Returns a `CallToolResult`: a JSON metadata text block (encoded and original size, captured `region`, `displays`, `cursor`, `backend` — `gdi` or `wgc`, whichever produced the frame — `cursorDrawn?`, `coordinateScale?`/`note?`, `flash?` when the post-capture glow was actually shown, `stages?` when `--profile-snapshot` is on) plus an `ImageContentBlock` (`output="inline"`, default; `"base64"` is an alias). `output="file"` saves to `%TEMP%\WindowsMcp` and returns the path in the metadata instead. Downscaled to fit `max_width`×`max_height` (1920×1080), with `scale`/`quality` on top; jpeg inline, png to file. `annotate:true` adds one `SnapshotAsync(desktop)` before the capture, draws a 2 px coloured box and a label chip (the snapshot's `el_N` ids) around every interactive element overlapping the captured rect, inserts the rendered element list as a second text block, and adds `annotated`/`annotations` to the metadata; `grid_columns`/`grid_rows` (0–64, no walk needed) overlay guide lines captioned with virtual-desktop coordinates and add `grid`. `backend` picks the frame source for this call: `auto` (default, defers to `--screenshot-backend`) / `gdi` / `wgc`; `wgc` errors when the compositor cannot serve the rect, `auto` falls back to `gdi` silently |
| `Ocr` | Extract text from the primary display, a `display` selection, or a region — same parser, always captured at full resolution |

---

### `ShellTools` — 1 tool
`src/WindowsMcp/Tools/ShellTools.cs`

**Injected:** `IPowerShellService`, `IJobService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Powershell` | `(string command, bool background, int timeout_seconds)` | Execute PowerShell; returns the `PSResult` as JSON (`{Success, Stdout, Stderr, ExitCode, Errors, TimedOut, StdoutTrimmedChars, StderrTrimmedChars}`). Foreground calls emit MCP progress heartbeats every 10s (via an SDK-injected `IProgress<ProgressNotificationValue>`, excluded from the tool schema) so spec-compliant clients reset their request timeout; the foreground execution backstop is 15 min. `timeout_seconds` (C-6) bounds this call to 1–900 s — outside that range, or combined with `background:true`, is an `ArgumentException` naming the parameter and raised before anything is spawned; `0` (default) means the backstop only. On expiry the child tree is killed and the result comes back with `TimedOut:true`, `Success:false`, `ExitCode:-1` and the partial stdout, not an exception. `background:true` starts a `JobService` job and returns its `JobInfo` immediately |

---

### `JobTools` — 1 tool
`src/WindowsMcp/Tools/JobTools.cs`

**Injected:** `IJobService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Job` | `(string mode, string? id, int tail)` | modes `status\|output\|cancel\|list` over background PowerShell jobs. Unknown ids are forgiving (`found:false` / `cancelled:false`). `tail` limits `output` to the last N chars per stream |

---

### `RegistryTools` — 3 tools
`src/WindowsMcp/Tools/RegistryTools.cs`

**Injected:** `IRegistryService` (+ the pure `RegistryGuard`, C-2's root denylist)

| Method | Description |
|--------|-------------|
| `RegistryGet` | Read a named value, or the whole key when `value_name` is omitted — `{Path, Values:[{Path, Name, Data, Kind}], SubKeys:[…]}`; an empty path lists the hive root, a missing key is an error naming it |
| `RegistrySet` | Write a value (String / DWord / QWord / Binary / MultiString / ExpandString); `confirm:true` |
| `RegistryDelete` | Delete a value, or the key itself when `value_name` is omitted (`recursive:true` when it has sub-keys); `confirm:true`, and `RegistryGuard` refuses the hive root and the profile/OS roots. Absent target is `existed:false`, not an error. Returns `{hive, path, valueName?, deleted, existed, subKeysRemoved?}` |

---

### `NetworkTools` — 2 tools
`src/WindowsMcp/Tools/NetworkTools.cs`

**Injected:** `INetworkService`, `IFirewallService`

| Method | Description |
|--------|-------------|
| `Network` | Adapters, listening ports (with owning process), Wi-Fi, DNS lookup, ping |
| `Firewall` | `list` / `add` / `remove` firewall rules (`confirm:true` for add/remove) |

---

### `WebTools` — 2 tools
`src/WindowsMcp/Tools/WebTools.cs`

**Injected:** `IWebService`, `IUIAutomationService` (C-5: the `source:"dom"` page walk), `IWindowService` (the frontmost Chromium window), plus the `TransportOptions` record (whether the transport keeps a session, i.e. whether sampling can reach the client) and the internal `ISamplingClient` seam (`Tools/ISamplingClient.cs`: `McpServerSampling` over `McpServer.SampleAsync` in production, a fake in the unit tests)

| Method | Description |
|--------|-------------|
| `Scrape` | `(url?, query?, source = "http", summarize = false, max_chars = 100000, window?)` → JSON `{Source, Url, Title, Chars, Truncated, Content, Summarized, Model, Note}`. `source:"http"` fetches the URL (http/https only, private address ranges rejected, DNS-rebinding aware) and converts the HTML to Markdown; `source:"dom"` reads the page already open in a Chromium browser — the `window` named, else the frontmost Chromium window in the A-1 inventory (Firefox is refused by name: no page document) — through one `SnapshotAsync(Window, …, UseDom:true)`, taking the first page that has a `DocumentId` and rendering it with `DomPage` (text plus one scroll hint). `Chars` is the size before `max_chars` cut it; `Truncated` covers that cut **and** a walk the element budget stopped early, whose `Note` names `--max-tree-elements`. `summarize:true` asks the client's model through MCP sampling (`ScrapeSummary` builds the request, 120 s bound): no capability, the stateless HTTP transport, blank text, no answer, or a failure returns the text with a `Note` saying which. Every refusal (`source`, `max_chars`, `url`/`window` against the source, `query` without `summarize`) names its parameter and runs before anything is fetched or walked |
| `HttpRequest` | HTTP request (GET/POST/PUT/DELETE/PATCH) with optional JSON headers and body; same http/https-only and private-range rejection, and a malformed `headers_json` is an `ArgumentException` naming the parameter |

---

### `DiskTools` — 1 tool
`src/WindowsMcp/Tools/DiskTools.cs`

**Injected:** `IDiskService`

| Method | Description |
|--------|-------------|
| `DiskInspect` | Disk usage analysis: usage (top dirs), reclaimable, file_types, stale |

---

### `StorageTools` — 1 tool
`src/WindowsMcp/Tools/StorageTools.cs`

**Injected:** `IStorageService`

| Method | Description |
|--------|-------------|
| `StorageHealth` | Diagnose disk/drive health (not usage): physical disks (model, bus/media type, SMART `HealthStatus` + reliability counters), per-disk online/offline + health, the volume→disk/partition map (filesystem, label, health), and recent disk-stack Error/Warning events. Metadata-first and hang-safe: free space is only probed when `include_usage:true`, each probe time-boxed in an in-process runspace, with an overall `CancellationToken` budget. `drive_letter` limits the volumes section. |

---

### `StartupTools` — 1 tool
`src/WindowsMcp/Tools/StartupTools.cs`

**Injected:** `IStartupReportService`

| Method | Description |
|--------|-------------|
| `StartupReport` | HiJackThis-style boot/persistence report. Sections: Run/RunOnce (all hives + per-user SIDs, enabled-state decoded), Startup folders, scheduled tasks, auto-start services, hosts, DNS, Winsock LSP, shell extensions, Control Panel applets (registry + `System32`/`SysWOW64` `*.cpl`), accessibility ATs, Image File Execution Options, Winlogon hooks, AppInit_DLLs, Active Setup, browser proxy, trusted-zone. Catalog-aware code-signing trust on every file-backed entry. `format`: `summary` (default, inline) / `json` / `text` / `both`; `includeProcesses` opt-in |

---

### `IntegrityTools` — 1 tool
`src/WindowsMcp/Tools/IntegrityTools.cs`

**Injected:** `IIntegrityService`

| Method | Description |
|--------|-------------|
| `Integrity` | File-integrity tripwire over a curated watch-list (hosts file, user + machine Startup folders, `~/.claude/settings.json`, `~/.gitconfig`, the `C:\` governance files). `mode`: `baseline` (SHA-256 snapshot to `%LOCALAPPDATA%\windows-mcp\integrity`, survives plugin upgrades) / `check` (added / removed / modified vs. baseline) / `list` (default watch-list + current baseline). `paths` adds extra semicolon-separated paths on `baseline` |

---

### `UsnTools` — 1 tool
`src/WindowsMcp/Tools/UsnTools.cs`

**Injected:** `IUsnService`

| Method | Description |
|--------|-------------|
| `FsChanges` | NTFS USN change journal — whole-volume file-change tracking from the OS journal. `mode`: `status` (journal id + `FirstUsn` / `NextUsn` / `LowestValidUsn`; record `NextUsn` now, query `since` it later) / `since` (records from `start_usn` forward, `max` default 200). `volume` default `C`. Requires elevation |

---

### `WatchTools` — 1 tool
`src/WindowsMcp/Tools/WatchTools.cs`

**Injected:** `IWatchService`

| Method | Description |
|--------|-------------|
| `Watch` | Live directory watching (`FileSystemWatcher`) with server-side buffering. `mode`: `start` (`path`, `filter` glob, `subdirs`; returns a session id) / `poll` (drain buffered created/changed/deleted/renamed events, `max` default 500) / `stop` / `list` (sessions with buffered/dropped counts). Events sit in a bounded ring (`EventRingBuffer`) between polls; oldest dropped when full |

---

## Service Interfaces (`WindowsMcp.Abstractions`)

Located in `src/WindowsMcp.Abstractions/`. Each interface is a separate file.

| Interface | Key Methods |
|-----------|-------------|
| `IInputService` | `ClickAsync`, `DragAsync` (and the B-2 overload `(…, durationMs, steps)`), `HoverAsync`, `TypeAsync` (and the B-1 overload `(text, TypeOptions)` → `TypeResult` with `Method`/`ClipboardRestored`), `PressKeyAsync`, `PressShortcutAsync`, `ScrollAsync` (and the B-3 overload `(…, shiftWheel)`), `KeyDownAsync(key)` / `KeyUpAsync(key)` (B-7: hold and release a modifier for `multi_select`; E-3: `click` modifiers and `key` `hold_ms`), `GetCursorPositionAsync` → `CursorPosition` |
| `IScreenshotService` | `CaptureAsync(region?, options?)` → `ScreenshotResult` (`CaptureOptions`: format, max size, scale, quality, cursor, annotations, grid, profile, backend) |
| `IFlashOverlay` | `Show(rect, duration)`, `Hide()`, `IsVisible` — the post-capture glow; every member is a silent no-op with no interactive window station |
| `IControlIndicator` | `BeginAction()` / `EndAction()` (a driving tool starts / ends; the last end starts the 5 s fade), `IsVisible`, `IsCaptureExcluded` (display affinity keeps both windows out of every capture), `SuspendForCapture()` → `IDisposable` (the windows are down until it is disposed, unless `IsCaptureExcluded`), `ConsumeTakeover()` → `TakeoverRecord?` (once, then null) — S-11; nothing refuses or blocks, nothing throws, and every member is a silent no-op when off, disposed or with no interactive window station |
| `IOcrService` | `ExtractTextAsync(region?)` → text |
| `IClipboardService` | `GetTextAsync`, `SetTextAsync` |
| `IAudioService` | `GetAsync` → `AudioState`, `SetVolumeAsync`, `SetMutedAsync` |
| `IPowerShellService` | `RunAsync(command)` → `PSResult` (the execution backstop only; an expiry is a caller-facing `TimeoutException`, C-6) and the C-6 overload `RunAsync(command, TimeSpan? timeout)` → `PSResult` (null = the backstop only; a non-positive timeout is an `ArgumentException`; an expiry **returns** `TimedOut:true` with the partial output) |
| `IJobService` | `StartAsync(command)`, `GetStatus(id)`, `GetOutput(id, tailChars)`, `Cancel(id)`, `List()` |
| `IUIAutomationService` | `GetStateAsync`, `FindElementAsync(text, kind, scope, windowTitle, includeOffscreen)`, `GetElementAsync`, `GetTextAsync`, `AssertElementAsync` → `AssertResult`, `InteractAsync` → `InteractResult`, `GetTableAsync`, `WaitForAsync(text, timeoutMs, intervalMs, kind, scope, windowTitle, includeOffscreen)` → `ElementInfo?` and the B-6 overload `WaitForAsync(WaitRequest)` → `WaitForResult`, `FocusAsync`, `SnapshotAsync(SnapshotRequest)` → `SnapshotResult` |
| `IFileSystemService` | `ReadTextAsync`, `ReadLinesAsync(path, maxBytes, encoding, offsetLines, limitLines)` → `TextWindow` (C-1), `ReadBytesAsync`, `WriteTextAsync(path, content, encoding, append, createParents)`, `CopyAsync(src, dst, overwrite)`, `MoveAsync(src, dst, overwrite)`, `DeleteAsync(path, recursive)`, `ListAsync(path, pattern, recursive, includeHidden, maxEntries)` → `FileListing` (round 4: bounded and cancellable; `startup_report`'s startup-folder scan passes `int.MaxValue` so its reach is unchanged), `SearchAsync`, `GetInfoAsync`, `HashFileAsync`, `ZipAsync`, `UnzipAsync` — C-1's flags are **required** parameters, not defaulted: `FileTools` is the only caller and a required flag cannot be forgotten |
| `IFileStreamService` | `GetStreamsAsync(path)` → `FileStreamsDto` (alternate data streams + reparse target) |
| `IRegistryService` | `GetAsync`, `SetAsync`, `EnumerateValuesAsync`, `EnumerateSubKeysAsync`, `ListAsync(hive, path)` → `RegistryKeyDto` (C-2: values + immediate sub-keys; an absent key is a `KeyNotFoundException`, unlike the enumerators' empty arrays), `DeleteValueAsync` → `bool` (whether it existed), `DeleteKeyAsync(hive, path, recursive)` → `RegistryKeyDeleteResult` |
| `IServiceControlService` | `ListAsync`, `GetStatusAsync`, `StartAsync`, `StopAsync`, `RestartAsync` |
| `IEventLogService` | `QueryAsync` |
| `ITaskSchedulerService` | `ListAsync`, `ListDetailedAsync`, `GetAsync`, `CreateAsync`, `DeleteAsync`, `RunAsync` |
| `IProcessService` | `ListAsync(nameFilter?)` (no CPU sample — the name kill and the startup report enumerate through it) and the C-3 overload `ListAsync(ProcessListOptions)` → `ProcessDto[]` with `CpuPercent`, sorted and capped, `InspectAsync`, `StartDetachedAsync(command)`, `StartDetachedAsync(ProcessStart)`, `KillAsync(pid)` and the C-3 overload `KillAsync(pid, KillOptions)` → `KillResult`, `ListLineageAsync`, `GroupByRootAsync`, `KillGuardedAsync` (still on the interface; the tool now passes `KillOptions.ExpectedStartUtc` instead), `KillTreeAsync` |
| `IWindowService` | `ExecuteAsync(action, title, hwnd?)` → `WindowAction`, `BringToFrontAsync(title?, hwnd?)` → `ForegroundResult` (replaced `SwitchToAsync(title)` → `bool` in B-10), `LaunchAsync(app)` → `int` and the B-8 overload `LaunchAsync(app, waitForWindow, timeoutMs)` → `LaunchResult`, `SetBoundsAsync(title?, hwnd?, x?, y?, width?, height?, restoreFirst)` → `WindowBoundsResult` (B-9), `EnumerateMonitorsAsync`, `ListAsync(includeMinimized, includeHidden)` → `WindowInfo[]`, `GetActiveAsync()` → `WindowInfo?` |
| `IVirtualDesktopService` | `ListAsync()` → `VirtualDesktopInfo[]`, `GetCurrentAsync()` → `VirtualDesktopInfo?`, `GetWindowDesktopIdAsync(hwnd)` → `string?`, `IsWindowOnCurrentDesktopAsync(hwnd)` → `bool?` — read-only (phase 1); an unavailable registry key or COM object is an empty array / null, never a throw |
| `IAppCatalogService` | `ListAsync()` → `AppEntry[]` (both Start Menu folders' `.lnk` files + the WinRT package manager's app list entries, merged and cached 5 min), `ResolveAsync(name)` → `AppMatch` (exact → prefix → fuzzy 70+; a miss is a `KeyNotFoundException` naming the five nearest with their scores; an abbreviation that fits two names equally is an `ArgumentException` naming both, and does not refresh the cache) |
| `IWmiService` | `QueryAsync(className, properties?, where?)` → rows |
| `IStorageService` | `GetHealthAsync(driveLetter?, includeUsage, timeoutSeconds)` → `StorageHealthReport` |
| `IDiskService` | `GetUsageAsync`, `GetFileTypesAsync`, `GetStaleAsync`, `GetReclaimableAsync` |
| `ISecurityService` | `AuditAsync()` → `SecurityAuditDto`, `GetDefenderStatusAsync()` → `DefenderStatusDto` |
| `ICertStoreService` | `ListAsync` → `CertInfoDto[]` (flags self-signed and expired) |
| `IReliabilityService` | `GetAsync(maxRecords)` → `ReliabilityReport` |
| `IDriverService` | `ListAsync()` → `DriverInfo[]` |
| `IFirewallService` | `ListAsync`, `AddAsync`, `RemoveAsync` |
| `IEnvService` | `GetAsync`, `SetAsync`, `ListAsync` |
| `IPowerService` | `ExecuteAsync(action)` — shutdown / reboot / logoff / lock / sleep / hibernate |
| `INotificationService` | `ShowAsync(title, message, appId?)` → `NotificationResult` (C-4: in-process WinRT toast; `appId` null = the server's own id) |
| `INetworkService` | `ListAdaptersAsync`, `ListPortsAsync`, `GetWifiAsync`, `DnsLookupAsync`, `PingAsync` |
| `IWebService` | `ScrapeAsync(url, maxChars = 100000)` → `ScrapeResult` (C-5, replacing the string-returning overload; the scheme and private-address checks run first, `Chars` is the size before the cut), `RequestAsync(url, method, headers, body)` → `HttpResponseDto` |
| `IIntegrityService` | `BaselineAsync`, `CheckAsync`, `GetBaseline`, `DefaultWatchList` |
| `IUsnService` | `StatusAsync(volume)` → `UsnStatus`, `ReadAsync(volume, startUsn, max)` → `UsnReadResult` |
| `IWatchService` | `Start(path, filter, subdirs)` → `WatchSession`, `Poll(id, max)` → `WatchEvent[]`, `Stop(id)`, `List()` |
| `IAuthenticodeInspector` | `Inspect(path)` → `AuthenticodeInfo` (catalog-aware trust + signer) |
| `ILspEnumerator` | `Enumerate()` → Winsock catalog providers |
| `IShortcutResolver` | `ResolveTarget(lnk)` → `.lnk` target via IShellLink |
| `IStartupReportService` | `BuildAsync()` → aggregated `StartupReportDto` |

---

## Data Models (`WindowsMcp.Abstractions.Models`)

Located in `src/WindowsMcp.Abstractions/Models/` (one DTOs file per domain, 24 files):

| File | Key Types |
|------|-----------|
| `InputDtos.cs` | `ClickResult`, `DragResult`, `TypeResult` (trailing `Method`/`ClipboardRestored`, both defaulted), `TypeOptions` (`Clear`, `Caret`, `PressEnter`, `PaceMs`), `CaretPosition` (enum), `CursorPosition`, `MouseButton` (enum) |
| `ScreenDtos.cs` | `ScreenRegion`, `CaptureOptions` (trailing `Annotations`/`Grid`/`Profile`/`Backend`), `AnnotationBox`, `GridSpec`, `ScreenshotResult` (trailing `AnnotationsDrawn`/`Stages`/`Backend`), `StageTiming`, `ScreenshotOptions` (`Scale`, `Flash`, `Profile`, `Backend`), `ImageFormat` (enum) |
| `UIAutomationDtos.cs` | `ElementInfo` (trailing `Scroll`), `Bounds`, `ScrollInfo`, `ElementTree` (trailing `Truncated`/`ElementLimit`, omitted from JSON when default), `FindElementResult`, `FindKind` (enum), `FindScope` (enum), `TableData`, `InteractResult`, `AssertResult`, `SnapshotScope` (enum), `SnapshotRequest` (trailing `UseDom`), `UiTreeOptions` (`MaxElements`, `Profile`, `MaxCachedElements` — D-11, default 5000), `SnapshotElement`, `SnapshotScrollable`, `SnapshotPage`, `SnapshotResult` (trailing `Stages`/`Pages`, both omitted from JSON when null), `WaitCondition` (enum, B-6), `WaitRequest` (`Condition`, `Text`, `TimeoutMs`, `IntervalMs`, `Kind`, `Scope`, `WindowTitle`, `IncludeOffscreen`, `UseDom`), `WaitForResult` (`Satisfied`, `Condition`, `ElapsedMs`, `Attempts`, `Detail`, `Element?` omitted from JSON when null) |
| `WindowDtos.cs` | `WindowAction` (trailing `MatchStrategy`/`Score`/`Hwnd`), `MonitorInfo` (trailing `WorkArea`/`Orientation`/`EffectiveDpi`/`Scale`, all defaulted), `ForegroundResult`, `WindowBoundsResult` (B-9: `Window`, `Before`, `After`, `MatchStrategy`, `Score`, `Restored`), `WindowInfo` (trailing `DesktopId`), `WindowProbe`, `WindowState` (enum, serialised by name), `VirtualDesktopInfo` |
| `AppDtos.cs` | `AppEntry` (`Name`, `Kind` `shortcut\|packaged\|path`, `Target` — the `.lnk` path or the AUMID, `Source`), `AppMatch` (`Entry`, `Score`, `Strategy` `exact\|prefix\|fuzzy`), `LaunchResult` (`MatchedName`, `Kind`, `Score`, `Pid`, `Hwnd?`, `Title?`, `WindowDetected`, `Strategy`) |
| `ProcessDtos.cs` | `ProcessDto` (trailing `CpuPercent`, defaulted to 0 — C-3; the lineage and group rows do not carry it), `ProcessSort` (enum, C-3), `ProcessListOptions` (`NameFilter`, `SortBy`, `Limit`), `KillOptions` (`Graceful`, `GraceMs`, `ExpectedStartUtc?`), `KillResult` (`Pid`, `Name`, `Graceful`, `ExitedGracefully`, `Forced`, `WaitedMs`), `ProcessStart`, `ProcessDetailDto`, `ModuleInfo`, `ProcessLineageDto`, `ProcessGroupDto` |
| `PowerShellDtos.cs` | `PSResult` (success, stdout, stderr, exit code, parsed errors, plus the trailing C-6 `TimedOut` and `StdoutTrimmedChars`/`StderrTrimmedChars`, all defaulted) |
| `JobDtos.cs` | `JobInfo`, `JobOutput` |
| `FileSystemDtos.cs` | `FileInfoDto`, `FileSearchHit`, `FileEntry` (C-1: `Path`, `Name`, `IsDirectory`, `Size` — 0 for a directory, `Modified` UTC, `Hidden`, plus trailing `IsLink` — round 4b's junction/symlink marker, defaulted so the field is additive), `FileListing` (round 4: `Entries`, `Truncated`, `MaxEntries`), `TextWindow` (C-1: `TotalLines`, `Offset` 1-based, `Returned`, `Truncated`, `Content` joined with `\n`), `AlternateStreamInfo`, `FileStreamsDto`, `RegistryValueDto`, `RegistryKeyDto` (C-2: `Path`, `Values`, `SubKeys`), `RegistryKeyDeleteResult` (`Existed`, `SubKeysRemoved`), `ServiceDto`, `ScheduledTaskDto`, `ScheduledTaskDetailDto`, `EventLogEntryDto` |
| `NotificationDtos.cs` | `NotificationResult` (C-4: `Shown`, `AppId`, `Registered`, `Note`) |
| `SystemDtos.cs` | `WmiResultDto` |
| `NetworkDtos.cs` | `NetworkAdapterDto`, `PortInfoDto`, `WifiInfoDto`, `PingResult` |
| `FirewallDtos.cs` | `FirewallRuleDto` |
| `WebDtos.cs` | `HttpResponseDto`, `ScrapeResult` (C-5: `Source` `http\|dom`, `Url` after redirects with any credentials stripped, `Title`, `Chars` before the cut, `Truncated`, `Content`, plus the trailing `Summarized`/`Model`/`Note`), `TransportOptions` (C-5: `Stateless`, with the `Stdio` instance a tool built without one assumes) |
| `DiskDtos.cs` | `DiskUsageEntry`, `FileTypeEntry`, `StaleFileEntry`, `ReclaimableSpace` |
| `StorageDtos.cs` | `StorageHealthReport`, `PhysicalDiskInfo`, `DiskInfo`, `VolumeInfo`, `ReliabilityInfo`, `DiskEventInfo` |
| `SecurityDtos.cs` | `AuthenticodeInfo`, `LspProviderDto`, `SecurityAuditDto`, `DefenderStatusDto`, `CertInfoDto` |
| `ReliabilityDtos.cs` | `ReliabilityReport`, `MinidumpInfo`, `ReliabilityRecord` |
| `DriverDtos.cs` | `DriverInfo` |
| `StartupReportDtos.cs` | `StartupReportDto`, `StartupHeader` + one record per section (`RunEntry`, `StartupFolderEntry`, `StartupTaskEntry`, `StartupServiceEntry`, `HostsEntry`, `DnsEntry`, `LspProviderEntry`, `ShellExtensionEntry`, `ControlPanelAppletEntry`, `AccessibilityToolEntry`, `IfeoEntry`, `WinlogonHookEntry`, `AppInitDllEntry`, `ActiveSetupEntry`, `BrowserProxyEntry`, `TrustedZoneEntry`, `ProcessEntry`) |
| `IntegrityDtos.cs` | `IntegrityItem`, `IntegrityBaseline`, `IntegrityChange`, `IntegrityCheckResult` |
| `UsnDtos.cs` | `UsnStatus`, `UsnChange`, `UsnReadResult` |
| `WatchDtos.cs` | `WatchSession`, `WatchEvent` |
| `ControlIndicatorDtos.cs` | `HotkeyChord` (S-11: `Modifiers` in `RegisterHotKey` flags — `ModAlt` 1, `ModControl` 2, `ModShift` 4, `ModWin` 8 — `Key` as a virtual-key code, `Display` such as `Ctrl+Alt+Shift+Backspace`; `Default`), `ControlIndicatorOptions` (`Enabled`, `Hotkey`, `Linger` — 5 s in `Default`), `TakeoverRecord` (`At`, wall-clock UTC, and `Hotkey`) |

**Model pattern** — all DTOs are C# records:
```csharp
// Models/ScreenDtos.cs
public record ScreenRegion(int X, int Y, int Width, int Height);
public record ScreenshotResult(byte[] Bytes, int Width, int Height, ImageFormat Format,
    int OriginalWidth, int OriginalHeight, double CoordinateScale, string? CursorDrawn = null,
    int AnnotationsDrawn = 0, StageTiming[]? Stages = null, string Backend = "gdi");

// IAudioService.cs (small result types may sit next to their interface)
public record AudioState(int Level, bool Muted);
```

---

## Key Service Implementations

### `UIAutomationService`

Uses **FlaUI.UIA3** to walk the Windows Accessibility (UIA3) tree:
- `GetStateAsync()` — builds a three-level `ElementTree` rooted at the foreground window (falls back to the focused element, then the desktop, through `UiRoots.FirstAvailable` — so a rung that throws, as `FocusedElement()` does with nothing in the foreground, moves on instead of failing the call; `find_element`'s foreground scope and a foreground `snapshot` share the ladder); every element gets an `el_N` id in the element cache's bounded LRU (D-11). The descent spends an `ElementBudget` (`UiTreeOptions.MaxElements`, from `--max-tree-elements`, default 500) per node and stops when it refuses; the **root** then carries `Truncated: true` and `ElementLimit`, which are omitted from the JSON otherwise
- `SnapshotAsync(request)` — the whole desktop in one call. Header from `IWindowService` (window list, active window, monitors) and `IInputService` (cursor), each read once; roots by scope — `desktop` walks every non-minimised window topmost first, `foreground` the active entry (falling back to UIA's own foreground window when the inventory flags none), `window` matches a title exact-then-substring and otherwise throws naming up to 15 open titles. One `ElementBudget` (per-call `MaxElements`, else `UiTreeOptions`) covers the whole call on the STA thread; a window whose walk throws is logged and skipped. Each walked node gets an `el_N` id, and the ids the *previous* snapshot issued are evicted from the element cache (`ElementCache.EvictSnapshot`) when the next one starts — a `find_element` or `get_state` id issued in between survives. Snapshot ids never count against the LRU's capacity. `wait_for`'s `text_exists`/`focused_element` polls, `screenshot(annotate)` and `scrape(source:"dom")` call `SnapshotAsync` too, so they evict the same way. The pure `internal static Project` splits one walked node into an interactive element and/or a scrollable region and never lets a password's value out. With `UseDom` (A-5 phase 1) a window flagged `IsBrowser` is walked from its page instead: `FindPageDocument` looks for the `Document` whose AutomationId is `RootWebArea` and retries a bounded number of times (Chromium builds its accessibility tree lazily on the first query, with a plain `Document` query as the nudge and a pause as the fill-in time); found, the walk root becomes that document and the window contributes a `SnapshotPage`; not found, the window is walked whole and its page entry carries the note. With `UiTreeOptions.Profile` the result also carries `header`/`walk` `StageTiming`s, logged to stderr at Information
- `FindElementAsync()` — walks one window root at a time (foreground by default; `scope=window` resolves a title exact-then-substring against the top-level windows and names the open windows when nothing matches; `scope=desktop` walks them all). Every property read is guarded and each element is evaluated inside a catch, so an element that dies mid-walk is skipped rather than failing the call; the kind filter is pushed into a UIA `OrCondition` for descendants and applied client-side to the root. `kind=interactive` is upstream's control-type set plus `Document` (`InteractiveControlTypes`). Off-screen elements and empty bounds are dropped before the 20-result cap unless `includeOffscreen` — an `Edit` with real bounds is kept either way, because browsers over-report it as off-screen
- `InteractAsync()` — click / invoke / toggle / select / focus / type / expand / collapse / set_value / scroll_into_view. Each acts through a UIA pattern (Invoke, SelectionItem, Toggle, Value, ExpandCollapse, RangeValue, ScrollItem) or a physical fallback via `IInputService` (a click at the element's centre; keyboard entry when there is no writable ValuePattern — since B-1 that entry runs the `TypePlanner` plan, so a newline is an Enter press and long text is pasted) and returns an `InteractResult` naming what fired; an unsupported pattern throws `InvalidOperationException` naming the pattern, the control type and the name (D-10; it was a masked `NotSupportedException`) — never a silent no-op. The action name, `type`'s and `set_value`'s required value, and the no-value rule of `expand`/`collapse`/`scroll_into_view` (`""` counts as none) are validated **before** the element is looked at; the STA half runs inside `Refusing(..., acts: true)` — `acts: false` for `scroll_into_view`, which moves only the view — which refuses a control that reports `IsEnabled == false` **before** any pattern call or focus (UIA would otherwise activate a disabled Win32 control's window on the way to refusing). E-2's verbs: `expand`/`collapse` read `ExpandCollapseState` first (already there → `already <state>`, a leaf → refused) and re-read it (`now <state>`); `set_value` goes to `SetRangeValue` when the control has RangeValuePattern (read-only refused first; a finite invariant-format number within the provider's bounds, a NaN or inverted bound treated as unknown; `Detail` = the value re-read with the range — a Win32 trackbar's provider reports 0-100 percent of its own range), else to `SetTextValue` on a writable ValuePattern (read before and after, a write that changed nothing refused, a password never read; unreadable → `replaced the whole value`); `SetOrRejected` turns a provider's `ArgumentException`/`Win32Exception` into a refusal naming the control. No focus call and no keyboard fallback, though UIA's Win32 proxies may activate the window themselves. `scroll_into_view` calls ScrollItemPattern (`now on screen, centre (x,y)` / `the control still reports offscreen`); without it an element `OnScreenCentre` proves visible (not offscreen, bounds with area) is `Method:"None"`, otherwise `NoScrollItem` names the nearest ancestor `CanScroll` accepts (ScrollPattern **and** a scrollable axis, as `UiClassifier.IsScrollable`) within 25 control-view levels, via the internal `ParentOf` delegate (a test seam), by a newly cached `el_N`; a `TimeoutException` in that walk propagates. `Describe` clips the control name to 80 characters (`UiRefusals.Clip`). `FocusAsync()` sets keyboard focus under the same `acts: true` rule
- `WaitForAsync(text, …)` — the original overload: polls `FindElementAsync` (same kind/scope/window/off-screen filters, the window re-resolved each poll) via the pure `PollAsync` loop: polls at least once, retries a poll that throws, clamps the sleep to the remaining budget, returns `null` when clean polls found nothing, and throws `TimeoutException` when *every* poll failed. Still on the interface; no tool calls it since B-6
- `WaitForAsync(WaitRequest)` (B-6, roadmap C4) — the conditional wait behind today's `wait_for`. It validates the request (`TimeoutMs` 0–120000, `IntervalMs` 0–5000, non-blank `Text`), then picks the *evidence gatherer* the condition needs and no more: `active_window` reads `IWindowService.ListAsync` only (**no UI walk**), `text_exists`/`focused_element` take one `SnapshotAsync` of the scope mapped by `SnapshotRequestFor` (no tree, the server's budget, `UseDom` carried through), the element conditions run `FindElementAsync`. `WaitLoopAsync` — separated from UIA so it is unit-testable with a fake gatherer — polls immediately and then every `IntervalMs` (10 ms floor, clamped to the remaining budget), counts every poll in `Attempts`, judges each with the pure `WaitConditions.Evaluate`, and **always returns a `WaitForResult`**: a timeout is `Satisfied:false` with the last `Detail`; a poll that throws is recorded and retried (D-5), and when *every* poll threw the detail is `every poll failed: <last message>` instead of a `TimeoutException`. D-11: an element poll's find mints up to 20 ids and the result carries at most one, so the loop hands the unused ids back to the cache (`release`, i.e. `ElementCache.Remove`) — a poll's ids just before the next poll runs, a satisfied poll's at once except its answer, a cancelled wait's pending ones; the final poll of a timed-out wait is kept so an id named in `Detail` still resolves (the leftovers are parity item D-18). The original overload releases every match but the first it returns
- `GetTableAsync()` — a missing GridPattern is `GridPattern not supported on <ControlType> '<name>'.` (D-10); reads cells via `IGridPattern` and column headers via the `TablePattern`; the raw strings are projected by the unit-testable `BuildTable`, so every header and cell is sanitised and a column with no header element is `""` rather than null
- `AssertElementAsync()` — exists / enabled / checked / visible / focused / value (`expected`: ordinal match against the ValuePattern value, else the Name — the same read as `get_text`); returns `AssertResult` with the observed state (focus owner, actual value, toggle state). A stale element (ProcessId 0, or UIA_E_ELEMENTNOTAVAILABLE / an RPC failure on a read — `IsElementGone`) fails with `element no longer available` instead of throwing; an id the cache no longer holds is `FAIL: exists — observed element id has expired (take a new snapshot)` for `exists` (or `unknown element id` when it was never issued) and the cache's `KeyNotFoundException` for every other state; optional properties a provider omits (modern Notepad's document has no `IsOffscreen`) fall back to UIA's defaults
- **Element ids (D-11).** `_cache` is an `ElementCache<AutomationElement>` sized by `ElementCache.CapacityFor(UiTreeOptions)` — the larger of `MaxCachedElements` (5000) and twice `MaxElements`, so the ids one `get_state` issues cannot evict each other. `GetElementAsync()` answers under the id it was given instead of minting a second one (every element-targeted `click`/`type`/`scroll`/`drag` resolves through it, so each used to grow the cache by one). A miss is a `KeyNotFoundException` that tells the two cases apart: an id this cache issued and no longer holds has *expired* (the message names the hidden snapshot takers and says to take a new snapshot), any other is *unknown*
- **Refusals from the target (D-10).** `get_text`, `get_table`, `interact_element` and `FocusAsync` (which no tool calls yet) run their STA work inside `Refusing(elementId, action, work, acts)`: a timeout, a cancellation or an `ArgumentException` is rethrown untouched (each further read of a hung target costs the UIA timeout on the one STA thread); anything else is checked against the element's liveness (`IsGone` — `UiRefusals.ProbeSaysGone` over one `ProcessId` read) and translated by `UiRefusals.Translate` into an `InvalidOperationException` — the element is gone (naming the id and "take a new snapshot"), a part of a live element (a cell, an item) is gone, the control or the item acted on is disabled, or a pattern/property is not supported. An exception it does not recognise stays masked

### `InputService`

Uses **H.InputSimulator** (`WindowsInput` namespace) for `SendInput` button, wheel and key events, and Win32 `SetCursorPos` for cursor placement:
- Cursor: `SetCursorPos(x, y)` in physical virtual-desktop pixels (origin = the primary monitor's top-left; monitors left of / above it have negative coordinates), then a `GetCursorPos` read-back — a point Windows clamped (off any monitor) throws `ArgumentOutOfRangeException` instead of clicking somewhere else. Button and wheel events carry no position, so they act at that cursor
- Mouse events: `LeftButtonClick` / `RightButtonClick` / `MiddleButtonClick`, `…ButtonDown/Up` for drags, `VerticalScroll` / `HorizontalScroll`
- `DragAsync(…, durationMs, steps)` (B-2) — press at the origin, then walk `DragPath.Points` with `SM_CXDRAG + 1` as the nudge, pausing `durationMs / steps` between points, then release; the release sits in a `finally`, so a cancelled drag never leaves the button down. Middle button is an `ArgumentException` naming `button` (H.InputSimulator has no middle down/up; D-10 — it was a masked `NotSupportedException`), a negative `durationMs` or `steps < 1` an `ArgumentOutOfRangeException`. The original `DragAsync(from, to, button)` keeps its press-jump-release behaviour
- `ScrollAsync(…, shiftWheel)` (B-3) — validates the direction *before* moving the cursor, then hovers the point and turns the wheel. `shiftWheel` holds `VK_SHIFT` (released in a `finally`) and sends the **vertical** wheel — up for `left`, down for `right` — and is an `ArgumentException` for `up`/`down`
- Keyboard events go through an internal `IKeyboardSink` seam (B-1): `SimulatorKeyboardSink` in production wraps `KeyPress`, `ModifiedKeyStroke` and `TextEntry`, and a recorder in the unit tests makes the *order* of a typing plan assertable without injecting input. Key names and chords are resolved by the pure `ShortcutParser`: named keys and aliases, `f1`–`f24`, numpad and media keys, single characters (`a`–`z` / `0`–`9` directly, anything else through `VkKeyScan` with the layout's implied Shift), `plus` for the `+` key, and bare keys such as `win`
- `TypeAsync(text, options)` (B-1) — executes the `TypePlanner` plan step by step against that sink, sleeping `PaceMs` between steps (never after the last one), and returns `TypeResult(text.Length, method, clipboardRestored)`. A `paste` step borrows the injected `IClipboardService`: read the current text, set the new text, `ctrl+v`, wait out a 150 ms settle (real desktop only — the target reads the clipboard on its own schedule), put the previous text back. `clipboardRestored` is `true` when it went back, `false` when the clipboard held no text or the restore failed, `null` when no paste happened. With no clipboard service, or when the borrow throws (another app holding it), the text is typed instead and the reported method degrades to `keys`. The single-argument `TypeAsync(text)` is exactly this with default options, so `interact_element`'s keyboard fallback and `file_dialog` get the same newline → Enter and long-text-paste behaviour
- `KeyDownAsync(key)` / `KeyUpAsync(key)` (B-7) — the modifier half of `multi_select`: the same `IKeyboardSink` seam gains `KeyDown`/`KeyUp`, resolved through `ShortcutParser.ResolveKey`, so the tool can hold Ctrl across a batch of clicks and the unit tests can assert the down/up order without injecting input. E-3 reuses the pair for `click(modifiers:…)` and `key(hold_ms:…)`; the release guarantees (reverse order, uncancellable token, one retry) live in `InputTools.HoldingAsync`, not here
- `GetCursorPositionAsync()` — `GetCursorPos` in those same virtual-desktop pixels; `screenshot` reads it once and hands it to the capture, so the reported position and the drawn mark cannot disagree
- Note: `MouseButton` enum disambiguation required — `H.InputSimulator` also exports `WindowsInput.MouseButton`; the abstractions define `WindowsMcp.Abstractions.Models.MouseButton` to avoid ambiguity

### `PowerShellService`

Executes foreground PowerShell via `System.Diagnostics.Process` (system `powershell.exe`):
- Serializes all calls through a `SemaphoreSlim(1,1)` gate; one clock — the earlier of the
  caller's `timeout` and the 15-minute execution backstop, started **after** the gate is acquired,
  so it bounds execution rather than queue-wait — tears down runaway scripts by killing the whole
  process tree. A `timeout` equal to the backstop is still the caller's clock; one millisecond
  past it is the backstop's, and only the backstop's reason says `(execution backstop)`
- C-6, two overloads, two behaviours on expiry: `RunAsync(command, timeout, ct)` (the `powershell`
  tool's) **returns** `PSResult` with `TimedOut:true`, `Success:false`, `ExitCode:-1` and
  `Errors` = the errors the script wrote then `"timed out after Ns"` last; `RunAsync(command, ct)`
  (every internal caller — disk, storage, security, firewall, network, audio, file streams) throws
  a caller-facing `TimeoutException` carrying that reason, because none of those callers reads
  `Success` and a partial stdout must never be parsed as an answer. Only the caller's own
  cancellation throws `OperationCanceledException`
- The invocation is built under the **caller's** token only, so a clock that fires during the
  temp-script write cannot leave through the exception path; a cancelled write deletes its partial
  `.ps1`
- Both streams are read by `Pump`s — chunked reads into a `BoundedTextBuffer` that can be
  inspected before the read finishes — under the caller's token only, so a tree kill closes the
  write ends and the pumps drain what the script wrote before it died (`HarvestAsync` waits out a
  2 s `HarvestGrace` for a survivor still holding a pipe, then takes what is buffered). Each
  stream is capped at `BufferCapacityChars` (1 000 000) with the tail kept, exactly as a job's is,
  and `StdoutTrimmedChars`/`StderrTrimmedChars` report what was dropped on every path
- Builds the invocation via the shared `PowerShellInvocation` helper: `-EncodedCommand`
  (base64 UTF-16LE, two-line preamble — UTF-8 console encoding, then
  `$ProgressPreference='SilentlyContinue'`) with a temp-`.ps1` `-File` fallback for oversized
  scripts; stdin is redirected and closed so the child cannot eat MCP protocol bytes
- `Stderr` is decoded through `ClixmlStderr`: Windows PowerShell 5.1 wraps every non-stdout stream
  in CLIXML when stderr is redirected, so progress records are dropped and error/warning/verbose/
  debug records become stream-prefixed text (`WARNING: careful`). Non-CLIXML and unparseable CLIXML
  pass through raw — losing output is worse than a blob
- `Errors[]` still holds only the `<S S="Error">` records (they alone decide `Success`), extracted
  through the same `ClixmlStderr` parser so the two cannot drift
- Returns `PSResult(Success, Stdout, Stderr, ExitCode, Errors, TimedOut, StdoutTrimmedChars,
  StderrTrimmedChars)` to callers
- On a timeout only stdout is usually there: Windows PowerShell 5.1 emits the `#< CLIXML` header
  as soon as a non-stdout record exists but buffers the records themselves until host shutdown, so
  a kill loses them. The harvest still decodes what arrived, and an unparseable bare header
  decodes to `""` rather than reaching the model

### `JobService`

Background PowerShell jobs (`powershell background:true` + the `job` tool):
- Spawns children via the same `PowerShellInvocation` helper as the foreground service, but
  **outside** the foreground serialization gate — jobs run concurrently (cap: 8 running; new
  starts are rejected when full)
- Per-job 60-minute backstop kills the process tree and marks the job `timedOut`; `Cancel`
  marks it `cancelled`; a per-job monitor task is the single writer of the final state
  (`completed`/`failed` by exit code otherwise)
- Stdout/stderr are pumped into `BoundedTextBuffer`s (~1 MB/stream, oldest chars trimmed,
  trim counters surfaced); the ~32 most recent finished jobs are retained, oldest evicted
- Stderr is decoded from CLIXML like the foreground tool's: **once** when the job finishes (in the
  monitor, before the state flips to a terminal value, rewriting the buffer via
  `BoundedTextBuffer.ReplaceAll` so `Tail`/`Length`/`TrimmedChars` stay consistent), and on read
  while a job is still running — `ClixmlStderr` drops a trailing partial document, so a mid-flush
  read still decodes the records that are complete. Stdout is never CLIXML and is left alone
- Registry pattern mirrors `WatchService`: `Dictionary` + lock, sequential ids (`j1`, `j2`…),
  forgiving unknown-id semantics

### `BoundedTextBuffer`

Thread-safe bounded text accumulator (lock + `StringBuilder`): keeps the most recent tail once
capacity is exceeded and counts trimmed chars — the unit-testable core of job output capture
(sibling of `EventRingBuffer`).

### `ScreenshotService`

Two frame sources (GDI or Windows.Graphics.Capture) + **SkiaSharp** downscale, annotate and encode. `IDisposable`, because the WGC path holds a D3D11 device; the container disposes the singleton:
- `ResolveBackend(requested, processDefault)` — pure: a call's `auto` becomes the process default (`--screenshot-backend`), anything else wins; the answer is lower-case and validated (`auto|gdi|wgc`) before anything is allocated, so a bad backend never costs a capture
- `AcquireFrame(rect, backend)` — `gdi` is the classic `Graphics.CopyFromScreen`, copied out of the locked GDI buffer into a writable `SKBitmap`; `wgc` goes to `WgcCaptureBackend` (one `GraphicsCaptureItem` per monitor the rect touches, each frame copied through a D3D11 staging texture and blitted into the rect) and **throws** naming the rect when the compositor cannot serve it; `auto` prefers WGC where it is supported and falls back to GDI silently. The returned pair carries the backend that actually produced the frame, which is what `ScreenshotResult.Backend` and the tool's `backend` metadata report — never `auto`
- `CaptureAsync(region?, options?)` — frame (null region = the primary display) → cursor → fit → downscale → encode. The frame — including an `auto` → GDI fallback — is acquired inside one `IControlIndicator.SuspendForCapture()` scope when the container injected an indicator (an optional constructor argument; S-11), so the ring and banner are never in the picture: a no-op where display affinity already excludes them, a hide-and-restore around the grab below Windows 10 2004. With `CaptureOptions.IncludeCursor` the pointer is composited onto the full-resolution frame first (real cursor icon through `DrawIconEx` on a GDI view over the Skia pixels, else `CursorOverlay.DrawRing`), then the bitmap is resized to `ScaleMath.Fit(...)` with a Mitchell cubic filter when that changes the size, and encoded as PNG or JPEG at `Quality`. With `CaptureOptions.Profile` the service adds `capture`/`cursor`/`resize`/`encode` `StageTiming`s and logs the same numbers to stderr at Information
- `EncodeAnnotated(bmp, format, quality, boxes, captured, coordinateScale, grid)` — the encode step both paths route through. With no boxes and no grid it is byte-identical to `Encode`; otherwise it copies the bitmap first (the unscaled path's `SKBitmap` is a zero-copy view of a read-only GDI lock), hands the copy to `Annotator.Draw`, and reports how many boxes landed. Drawing happens **after** the downscale, so a 2 px box and an 11 px chip stay legible at the output size and map through the same `CoordinateScale` the metadata reports
- Returns `ScreenshotResult(Bytes, Width, Height, Format, OriginalWidth, OriginalHeight, CoordinateScale, CursorDrawn, AnnotationsDrawn, Stages, Backend)`; the `screenshot` tool turns that into an image content block plus a metadata text block (`output="file"` writes to `%TEMP%\WindowsMcp` and returns the path instead)
- Which rect to capture is the tool's decision (`RegionMath` over `IWindowService.EnumerateMonitorsAsync`); the service captures whatever rect it is handed

### `WindowService` — matching, the foreground ladder, launching, geometry, monitor detail

B-8/B-9/B-10/B-12 — the window verbs, with every decision pushed into a pure helper:
- `ExecuteAsync(action, title, hwnd?)` and `BringToFrontAsync(title?, hwnd?)` both resolve their target through `WindowMatcher.Match` over the A-1 inventory (`ListAsync(includeMinimized:true, includeHidden:false)`), so a partial title acts on the window it names and `FindWindow` is gone from both paths. `ExecuteAsync` validates the verb before it reads the inventory and reports `Success:true` for the window it acted on — a title that matches nothing throws instead of returning `Success:false`
- `BringToFrontAsync` hands the match to `ForegroundLadder.Bring` with an `IForegroundNative` (`Win32ForegroundNative` in production, a recording fake in the tests), so which rung is tried and what is reported is unit-testable with no desktop
- `LaunchAsync(app, waitForWindow, timeoutMs)` (B-8) is the decision, not the mechanism: `IsPathOrExecutable` short-circuits an existing file or directory, or a name ending in `.exe` that resolves on `PATH` (a bare word like `calc` deliberately does **not** — it is a Start Menu name and belongs to the catalog even though `calc.exe` exists); anything else goes to `IAppCatalogService.ResolveAsync`, and the entry is started through the `IAppActivator` seam — `ActivatePackaged` for an AUMID, `StartShortcutOrPath` for a `.lnk` or a path. The inventory is read **before** the launch so a title match afterwards can only be a new window, then `LaunchWait.ForWindowAsync` polls. `timeoutMs` outside 1–60000 and a blank name are `ArgumentException`s; with no catalog registered (a bare `new WindowService()`), a non-path name is an `InvalidOperationException` saying so
- `SetBoundsAsync(title?, hwnd?, x?, y?, width?, height?, restoreFirst)` (B-9) validates the arguments before any window is read, resolves the target through the same `WindowMatcher` — or, when neither `title` nor `hwnd` is given, `GetActiveAsync` with `MatchStrategy: "foreground"` and `Score: 100` — and hands it to `WindowGeometry.Apply` over an `IWindowGeometryNative` (`Win32WindowGeometryNative` in production), so the flag composition, the state refusal and the re-read are testable with no desktop
- `EnumerateMonitorsAsync` reads `MONITORINFOEXW` (the same header plus the device name `EnumDisplaySettings` needs), so each `MonitorInfo` now carries `WorkArea` from `rcWork`, `Orientation` from the current display mode's `dmDisplayOrientation` × 90, `EffectiveDpi` from `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` and `Scale` = `EffectiveDpi / 96.0`. Both extra reads are guarded and fall back to `96` / `0` rather than dropping the monitor, and `Index` is still the position in the returned array so `screenshot`/`ocr` `display` selection is unaffected

### `AppCatalogService`

B-8 / roadmap C7 — every application this machine can launch by name, built in-process (no `Get-StartApps`, so no PowerShell cold start and no serialization gate):
- Two sources, both behind constructor seams so the cache and the merge are unit-testable: every `*.lnk` under the machine-wide and per-user Start Menu `Programs` folders (recursive through `ScanStartMenu(roots)`: `*.lnk` ignoring case, Hidden and System folders included, at most `MaxStartMenuDepth` = 8 folders below a root so a junction back to it cannot loop, an unreadable folder skipped by `IgnoreInaccessible` and a walk that still breaks keeping what it had read — so one bad folder never drops every shortcut; the file name is the app's name and the `.lnk` path is the launch target), and every packaged app the WinRT `PackageManager.FindPackagesForUser("")` → `Package.GetAppListEntriesAsync()` yields (display name + AppUserModelId, `Source: package:<family name>`). A source that throws contributes an empty list rather than emptying the catalog, and a package that refuses `GetAppListEntriesAsync` is skipped
- `ListAsync()` reads the sources at most once per `CacheTtl` (5 minutes, measured against an injectable `TimeProvider`) behind a `SemaphoreSlim`; `ResolveAsync(name)` that misses refreshes **once** — the app may have just been installed — and then the miss stands until the TTL turns over
- The rules are the pure `AppCatalog`: `Merge` deduplicates by name (ordinal, ignoring case) with a shortcut beating a packaged entry of the same name and orders by name; `Match` is exact → prefix (shortest name wins) → fuzzy (`max(FuzzyMatch.PartialRatio, TokenSetRatio)`, or `AbbreviationScore` 95 less the unread words when `FuzzyMatch.AbbreviationUnread` says the request abbreviates the name and that is higher; `>= 70`, highest wins, ties to the shortest name), and no match is a `KeyNotFoundException` naming the request and the five nearest entries with their scores. A top-score tie that an abbreviation is part of is an `ArgumentException` naming every tied entry with its score (`'wt' could mean more than one app: 'Windows Terminal' (95), 'Windows Tools' (95). Use more of the name.`); `ResolveAsync` refreshes the cache only for the `KeyNotFoundException`, not for that
- Starting an app is `Win32AppActivator` behind `IAppActivator`: `IApplicationActivationManager.ActivateApplication` for an AUMID (the only API that hands back the pid a window wait needs; the leading method only, per the COM rule) and `Process.Start(UseShellExecute:true)` for a `.lnk` or a path. Failures are D-10's `ProcessStartErrors`: a failed activation is an `InvalidOperationException` naming the AUMID and the HRESULT in hex (no longer `Marshal.ThrowExceptionForHR`), and a failed start goes through `ProcessStartErrors.Start`

### `VirtualDesktopService`

A-12 phase 1 — read-only virtual-desktop facts, from the registry through `IRegistryService` and from the documented `IVirtualDesktopManager` (declared in vtable order per the COM rule):
- `ListAsync()` — the `VirtualDesktopIDs` blob (16 bytes per desktop, in order) parsed by the pure `VirtualDesktopRegistry`. When the blob is absent — as on this Windows 11 build — the list falls back to the `…\VirtualDesktops\Desktops` subkey names in enumeration order, and the current desktop to `SessionInfo\<id>\VirtualDesktops\CurrentVirtualDesktop` and then to the desktop the foreground window is on. Names come from each desktop's `Name` value, else `Desktop N`
- `GetCurrentAsync()` — the `IsCurrent` entry of the same list, so `window desktops` reports one truth for `current` and `all`
- `GetWindowDesktopIdAsync(hwnd)` / `IsWindowOnCurrentDesktopAsync(hwnd)` — the COM manager, created once behind a lock and never retried after a failure. `WindowService` takes the service as an **optional** constructor dependency and calls the first of these once per window that survives `WindowFilter`, inside a catch, to fill `WindowInfo.DesktopId`; a host that has not registered it (or any of the direct `new WindowService()` call sites) simply reports a null id. Every failure path (no key, no COM object, `GUID_NULL`) is an empty array or a null: the desktop is decoration on the window list and must never be the reason it fails

### `FlashOverlay`

A-14 — the post-capture glow, and the only signal a person at the target machine gets that an agent captured their screen:
- A `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` window on its own thread with its own message loop, painted by `UpdateLayeredWindow` from a Skia bitmap the pure `FlashGlow` draws. Click-through, non-activating, and a tool window, so it is invisible to `window list` and to `snapshot`. The window itself — class registration, creation, the DIB paint, the top-most show — is the shared `LayeredWindow` helper (S-11), whose one static window procedure outlives every instance that registers a class
- `Show(rect, duration)` hides any glow already up and replaces it; `Hide()` is idempotent and is called before **every** capture, whatever `--flash` says, so the glow can never be in a picture. The thread only starts on the first `Show`, so an off switch costs nothing
- Nothing here throws: with no interactive window station (Task Scheduler, session 0) every member is a silent no-op and `IsVisible` stays false — which is why the tool reports `flash` from `IsVisible` rather than from the switch

### `ControlIndicator`

S-11 — the "AI is in control" ring and banner, and the user's takeover by key combination. Information, never control: nothing here refuses, delays or cancels a tool call, and no physical input is blocked:
- **Lifetime.** The call-tool filter calls `BeginAction()` before and `EndAction()` after every tool in `Hosting/DrivingTools.Names` (`click`, `drag`, `hover`, `type`, `key`, `shortcut`, `scroll`, `multi_select`, `multi_edit`, `interact_element`, `file_dialog`, `focus`, `switch_to_window`). The pure `ControlIndicatorState` refcounts the calls in flight (HTTP clients can overlap), clamps an unbalanced end at zero, and keeps the indicator visible until `Linger` (5 s) after the last end — measured on the monotonic clock, so a wall-clock step back cannot hold it up. A `BeginAction` during the fade cancels the fade
- **Windows.** A lazily started STA thread owns two `LayeredWindow`s of one shared class: a ring around the pointer and a pill banner centred just below the top of the work area of the pointer's monitor, both painted by the pure `ControlIndicatorArt` at that monitor's DPI. One tick — every 16 ms while shown, 100 ms otherwise — asks the state whether to show, so it is both the pointer follower and the fade; the ring is repainted only when the DPI changes and the banner only when the monitor changes. `BeginAction` runs a tick on the thread and waits at most 50 ms for it
- **Takeover.** The combination is `RegisterHotKey`'d against the banner (with `MOD_NOREPEAT`) only while the indicator is shown and unregistered when it hides. `WM_HOTKEY` is taken off the thread's own queue (the window class is shared, so its procedure cannot know which instance it serves); a press while shown dismisses the indicator even with calls in flight and records `TakeoverRecord(At, Hotkey)`, the latest winning; `ConsumeTakeover()` hands it out once. A combination that cannot be registered leaves the hint off the banner and logs one warning per instance
- **Never in a picture.** On Windows 10 2004+ (build ≥ 19041) both windows get `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` and `IsCaptureExcluded` is true, so `SuspendForCapture()` returns a no-op scope. Below that build the affinity would black-box the windows instead, so none is applied and `SuspendForCapture()` takes them down synchronously before the grab and lets them back once the last open scope is disposed; with no overlay thread yet there is nothing on screen and no thread is started
- **A courtesy, never a failure.** Every member is a silent no-op when `--control-indicator off` (nothing is created and nothing registered), after `Dispose`, or with no interactive window station; the filter also swallows anything the indicator throws, so it can never fail a tool call
- **Seams** (`internal`, for the tests): `ExcludeFromCapture` (false lets a capture see the windows), `OsBuild`, `OnShowForTest`, `RingHandle`/`BannerHandle`, `IsHotkeyRegistered`. `Hosting/HotkeyParser` turns `--control-hotkey` text into a `HotkeyChord` through `ShortcutParser.ResolveKey`: modifiers then one key, with ctrl, alt or win **written** (shift alone, or a modifier the layout implies for the key, does not count; implied modifiers are still registered)

### `ProcessService` — the CPU sample and the graceful kill

C-3 — the two impure halves around the pure `CpuSample`:
- `ListAsync(ProcessListOptions)` reads every candidate's `TotalProcessorTime` with a `Stopwatch` timestamp **per process**, waits one `CpuSampleWindow` (250 ms), reads them again, and normalises each with its own elapsed time — walking a few hundred processes takes tens of milliseconds at either end, and one shared window over-counted the processes read first until the whole box summed to 155 %. A process that exits between the readings, or one whose CPU time is unreadable, reads 0. The `ListAsync(nameFilter)` overload is untouched and does **not** sample: the name kill and `startup_report` enumerate through it and must not pay the window
- `KillAsync(pid, KillOptions)` runs the start-time guard first when one is given (a mismatch aborts and kills nothing, unchanged), then — only when `Graceful` — posts `WM_CLOSE` through the internal `IProcessWindowNative` seam to every **visible** top-level window the pid owns (`EnumWindows` + `GetWindowThreadProcessId` + `IsWindowVisible` + `PostMessage` in `Win32ProcessWindowNative`; a hidden helper window must not make a windowless process look like one that was asked to close), waits for the exit bounded by `GraceMs`, re-reads `HasExited` once when that wait times out — a process that answered the close late is `ExitedGracefully:true, Forced:false`, not forced (round 4) — and calls `Kill()` only if it is still there (`Kill()` on a process that exited meanwhile does not throw on .NET 10, so nothing catches around it). A caller cancellation during the grace wait rethrows and kills nothing: the process has been asked to close and is left to answer. Nothing to post to = forced at once with `ExitedGracefully:false, Forced:true, WaitedMs:0`. The seam and the sample delay are both injected through an `internal` constructor, so the kill path and the sampler are testable with no desktop and no real 250 ms

### Refusals at the OS boundary (D-10)

`Process.Start`, the registry API, the power calls and the task scheduler report a refusal as a
type `ToolErrors.IsCallerFacing` does not accept (`Win32Exception`, `SecurityException`,
`COMException`, `FormatException`), so each is translated where it is called:
- `ProcessStartErrors` (`internal static`) — `Start(psi, what, notFoundHint?)` wraps `Process.Start` for `start_process` (`ProcessService.StartDetachedAsync`), `launch` by path (`Win32AppActivator.StartShortcutOrPath`) and the legacy launch (`WindowService`): Win32 error 2/3 is a `FileNotFoundException` naming the file actually tried — for an unquoted command line, the text before the first space, with a hint to quote it or use `args_json` — 5 an `UnauthorizedAccessException`, any other OS error an `InvalidOperationException` with its message; `ActivationFailed(aumid, hr)` names the AUMID and the HRESULT in hex. A null process (ShellExecute handed the target to something already running) is still reported as a failure — parity item D-17
- `RegistryErrors` (`internal static`) — `RegistryService` runs every operation inside `RegistryErrors.Guard(hive, path, …)`, so access-denied is one `UnauthorizedAccessException` naming the hive (as the caller gave it) and the key on every path; a descendant refused inside the tree count keeps its own, more precise name, and a `DeleteSubKeyTree` denied part-way says sub-keys may already be gone (`partial: true`; atomicity is parity item D-12)
- `PowerService` takes an `internal` `IPowerNative` seam (`Win32PowerNative` in production, created by the public constructor, so the DI registration is unchanged): each action reports `false` plus the Win32 error, and `PowerService` throws `InvalidOperationException` `Windows refused the power action '<action>': <reason> (Win32 error N).` with a hint for sleep, hibernate and lock. Only `Win32PowerNative.EnableShutdownPrivilege`'s token plumbing still throws a masked `Win32Exception` — the one exemption `ToolErrorsTests` names for that type
- `TaskSchedulerService.ParseTrigger` — `create` accepts one trigger form, a date-time parsed with the invariant culture; anything else is an `ArgumentException` naming `trigger`, thrown before the scheduler is touched

`ToolErrorsTests` scans the IL of `WindowsMcp` and `WindowsMcp.Abstractions` for every exception
construction and fails on a type that is neither caller-facing nor on its site-keyed
`MaskedOnPurpose` list, and on a `Marshal.ThrowExceptionForHR`/`GetExceptionForHR` call outside a
reasoned list. It cannot see an exception a library throws on the server's behalf; those still
need translating at the call site, as above.

### `FileSystemService` — verify, set aside, commit or restore

C-1 round 4 — every mutating verb runs its refusals **before** it touches anything, and never
deletes what it is replacing until the replacement has landed:
- **The order of the checks** on `CopyAsync`/`MoveAsync`: `RefuseRootsAndSelfContainment` (a volume or share root at either end, the same path, a destination inside the source or one that contains it) → `RequireSource` (a `FileNotFoundException` naming a source that is not there) → `RefuseExistingDestination` (the `overwrite` gate). `DeleteAsync` refuses a root the same way. `PlainPath`/`StripExtendedPrefix` normalise `/` to `\` and strip a `\\?\` or `\\?\UNC\` prefix **only** when a drive or UNC path follows, so `\\?\Volume{…}\` stays as it is and is refused as a root; anything left that is not fully qualified (`\\?\C:`, `\\?\C:relative`) is refused as "not a plain absolute path"
- **`Canonical`** compares the volume's own spelling of both paths, not the string the caller sent: `PathCanonical.Canonical` asks `IFinalPathNative.FinalPathOf` for the deepest existing ancestor's final path and appends the segments that do not exist yet, so a `subst` letter, a mapped network drive, a junction and a symlink all reduce to the same directory. Containment is judged on **both** the as-written full paths and the canonical ones — a destination that is itself a link out of the source is literally inside it — and `RefuseLeafLinkToRoot` walks a leaf link's targets hop by hop (at most 32, visited set for cycles) to refuse a link chain that ends at a volume root. `SameRoot` (which decides the cross-volume copy-then-delete fallback) is true only when the roots agree **both** as written and canonically, because `Directory.Move` itself refuses roots that differ as written
- **The aside** (`Aside.Take/Commit/Restore/NotRestored`) is how `overwrite:true` replaces: an existing destination is renamed to `<dst>.replaced.<guid>`, the copy or move runs, `Commit()` removes the aside **after** the `try` (so a failing commit can never run `Restore` over a finished copy), and any failure or cancellation calls `RollBack` — restore the destination, remove the parent directories this call created (`CreateParent` records them as it makes them), and rethrow. A restore that could not put the destination back wraps the caller's exception in an `InvalidOperationException` that keeps it as `InnerException` and names both the partial destination and the aside holding the previous content; `TwoSentences` punctuates the join. A cross-volume move commits the aside **before** removing the source, so nothing is ever in zero places, and reports what remains at the source if the removal fails
- **`DeleteTree`** is the one tree remover behind `delete recursive:true`, the aside's commit and restore, and the cross-volume source removal: a directory reparse point goes as a link without being descended into, the read-only attribute is cleared on files and directories first (the caller already confirmed the delete), and files go before directories. It observes the cancellation token per entry for `delete`; the aside and the source removal pass none — they must finish
- **`ListAsync`** is a `FileSystemEnumerable` whose `ShouldRecursePredicate` refuses reparse points, so a junction or symlink is one `FileEntry` with `IsLink:true` and never a descent; the walk observes the token per entry, stops at `maxEntries` with `Truncated:true`, and refuses a `pattern` holding a path separator and a `path` that is a file (with any trailing separators trimmed)
- **Nothing partial is left behind:** `WriteTextAsync`'s temp file is deleted on every failure path (the third `IOException` of the rename retry, an access-denied target, a cancel during the delay), and `ZipAsync` builds beside the target and `File.Move`s into place — `ZipFile.CreateFromDirectory` creates the archive before it reads the source, so a failing zip used to leave a valid **empty** archive where the caller's previous one was

The final-path seam is `internal`: `IFinalPathNative` / `Win32FinalPathNative` (`CreateFile` with
`FILE_FLAG_BACKUP_SEMANTICS` and no access rights, then `GetFinalPathNameByHandle` with
`VOLUME_NAME_DOS`, both added to `NativeMethods.txt`; the path is opened in its extended-length
form so an ancestor beyond 260 characters still resolves, and the `\\?\` / `\\?\UNC\` prefix is
stripped off the answer). `null` means Windows cannot say, and the caller falls back to the path
as written. `FileSystemService`'s public constructor uses `Win32FinalPathNative.Instance`; an
`internal` one takes a fake, which is how `PathCanonical` is unit-tested without a second volume.

### `WebService` — the fetch, the title, the depth bound

One `HttpClient` per service (and the service is a process singleton; an `internal` constructor
takes a shorter timeout so a test can prove the timeout path):
- **`ValidateUrlAsync` decides the scheme first** (C-5 review F5): anything but `http`/`https` is
  an `ArgumentException` naming the scheme — `HttpClient` would otherwise raise a masked
  `NotSupportedException`, and a scheme with no host resolved `""`, this machine, for the address
  check. Then the private-address check as before: every resolved address of the host, so a DNS
  rebind is caught too. Both run before anything is fetched, for `scrape` **and** `http_request`
- **`ScrapeAsync(url, maxChars, ct)`** reads the response, takes `Title` from AngleSharp's
  `IDocument.Title` (whitespace collapsed, `null` when absent or blank — not a regex, so a
  commented-out tag, an SVG tooltip or a `<title>` inside a script is not the page's title),
  reports `Url` as the response's request URI (so a redirect is reported at its destination) with
  any user info stripped and every escape intact (`AbsoluteUri`), converts the HTML with
  `ReverseMarkdown`, and cuts to `maxChars` through `TextCap` — `Chars` is the length before the
  cut
- **Failures are answers, not faults:** a 404 or a refused connection becomes an
  `InvalidOperationException` naming the URL, and the client's own timeout a `TimeoutException`
  naming the URL and the seconds waited, so both reach the caller instead of the SDK's masking
- **`MaxNestingDepth` (300)** — the parsed tree's depth below `<body>`, measured iteratively with
  an explicit stack (`NestingDepth`), is refused past 300 naming the URL and the limit:
  `ReverseMarkdown` recurses once per level and overflowed the stack near 900, which no `catch`
  can stop and which took the whole server down mid-call

### Pure helpers (`ScaleMath`, `RegionMath`, `CursorMath`, `CursorOverlay`, `Annotator`, `FlashGlow`, `ControlIndicatorArt`, `UiText`, `WindowFilter`, `VirtualDesktopRegistry`, `FuzzyMatch`, `WindowMatcher`, `ForegroundLadder`, `AppCatalog`, `LaunchWait`, `WindowGeometry`, `ArgvJson`, `TypePlanner`, `DragPath`, `BatchTargets`, `RegistryGuard`, `LineWindow`, `CpuSample`, `PathCanonical`, `DomPage`, `ScrapeSummary`, `TextCap`)

`internal static` classes in `Services/` with no Win32, no screen and no UIA dependency, so every
rule is unit-tested headless:
- `ScaleMath.Fit(origW, origH, maxW, maxH, userScale)` — fit inside the cap (cap ≤ 0 = ignored), apply the user scale, never upscale; returns the output size and `CoordinateScale` = origW / Width
- `RegionMath` — `ParseRegion("x,y,w,h")`, `ParseDisplays("all" | "0,2")`, `Union`, `VirtualScreen`, `Primary`, and `Validate`, which **rejects** a region outside the virtual screen rather than clipping it. Shared by `screenshot` and `ocr` so the two cannot drift
- `CursorMath.MonitorIndexOf(x, y, monitors)` — the monitor a virtual-desktop point sits on, `-1` for none
- `CursorOverlay` — `RingPoint` (cursor rebased onto the captured rect, null when outside) and `DrawRing` (white 3 px ring at radius 12, black 2 px at radius 8)
- `Annotator` — A-6's drawing core (SkiaSharp only, no screen): a twelve-colour opaque `Palette` indexed by list position via `ColorFor`, so a colour always means the same label even when an off-image box is skipped; `ToImage` maps virtual-desktop `Bounds` to image pixels (subtract the captured origin, divide by the coordinate scale, round half **away from zero**, widen a sub-pixel box to 1 px, clip — null when nothing is in the picture); `ChipRect` places the label chip just above the box's top-left, inside the box when there is no room, never off the image; `UseDarkText` picks black or white by luminance; `Draw` paints the grid first, then each box as a 2 px stroke plus a filled chip, and returns how many were drawn. Grid lines are translucent dark grey at every interior division, captioned with the **virtual-desktop** coordinate, not the image pixel
- `FlashGlow` — A-14's glow as pixels: `WindowRect` inflates the captured rect by the 10 px band, `Render` paints an orange band fading outward with the captured area left fully transparent, so the picture underneath is untouched
- `ControlIndicatorArt` — S-11's indicator as pixels and positions, blue so it is never mistaken for the orange flash: `CursorRing(dpiScale)` (a 56 px square at 96 DPI — a soft halo, a solid stroke and a thin white inner line, the centre left transparent so the pointer is never tinted), `Banner(text, dpiScale)` (a 32 px-high dark pill sized to the text, a blue dot and white Segoe UI text), `BannerText(hotkey?)` (the hint is dropped when the combination could not be registered), `RingRect` (centred on the pointer) and `BannerRect` (centred in the work area, 12 px below its top at 96 DPI). Premultiplied BGRA for `UpdateLayeredWindow`; a non-positive or NaN scale is an `ArgumentOutOfRangeException`
- `VirtualDesktopRegistry` — A-12's core over the two Explorer registry blobs: `Parse(ids, current, nameOf)` yields one `VirtualDesktopInfo` per complete 16-byte GUID in registry order (a trailing partial GUID is ignored), flags the one `current` names, and falls back to `Desktop N` when no name is stored; `Id` is the wire form (lower-case, dashed, no braces) and `GuidKey` the registry subkey form (upper-case, braced)
- `FuzzyMatch` — B-10's three `thefuzz` scorers, in-repo and package-free: `Ratio` (`round(200 × LCS / (|a| + |b|))`, away from zero), `PartialRatio` (the best `Ratio` of the shorter string against every same-length window of the longer) and `TokenSetRatio` (lower-case, split on every non-alphanumeric run, best of the three shared/own token comparisons). Every score is 0–100 and case-insensitive; two empty strings score 100, one empty against a non-empty 0. Beside them, B-8's `AbbreviationUnread(request, candidate)` (and `AbbreviationCovers`): -1 unless the request's words read the name's words in order from the first, none skipped, each request word of 2+ characters as a word prefix or the initials of the next two or more (an all-initials reading must reach the last word), else the fewest name words left unread — `vs code` → Visual Studio Code 0, `wt` → Windows Terminal 0; memoized, so polynomial. `WindowMatcher` does not use it
- `WindowMatcher.Match(inventory, title?, hwnd?)` — the single title-to-window resolver for `switch_to_window`, `focus` and `window(action:…)`: an `hwnd` wins and never fuzzes, else exact (ordinal, ignoring case) → substring → fuzzy (`max(PartialRatio, TokenSetRatio) >= 70`), ties inside one strategy going to the lowest `ZOrder` (the frontmost). Minimised windows are candidates. Neither argument is an `ArgumentException`; nothing matched is a `KeyNotFoundException` listing up to 15 open titles and the nearest score. Deliberately **not** `UIAutomationService.MatchWindows`, which stays exact-then-substring — a snapshot scope must not fuzz
- `ForegroundLadder.Bring(match, native)` — restore if `IsIconic`, then `SetForegroundWindow`, then `AttachThreadInput` + `BringWindowToTop` + `SetForegroundWindow` + detach (the whole rung is skipped when the attach is refused, the usual elevated-target case), then the ALT nudge. `GetForegroundWindow` is re-read after every rung and is the only source of `ForegroundResult.Success`/`Strategy`; `SetForegroundWindow`'s own return value is never consulted. The Win32 sits behind `IForegroundNative`/`Win32ForegroundNative`, so the ladder is headless-testable
- `AppCatalog.Merge(shortcuts, packaged)` / `Match(catalog, name)` — B-8's catalog rules with no Start Menu, no package manager and no clock attached: the merge (dedupe by name, a shortcut beating a packaged entry, ordered by name) and the resolution (exact → prefix → fuzzy `>= 70` with the abbreviation score, ties to the shortest name unless an abbreviation is in the tie, which is refused as ambiguous, a miss listing the five nearest with their scores)
- `LaunchWait.Pick(inventory, pid, matchedName, before)` / `ForWindowAsync(...)` — B-8's window wait: out of one inventory reading, a window whose `Pid` is the launched one (frontmost first — the strongest evidence), else a window that was **not** open before the launch whose title matches the resolved name exact → substring → fuzzy (`>= 70`), because packaged apps and browsers hand off to a process the activation never named. `ForWindowAsync` polls immediately and then every `DefaultPollMs` (250 ms), clamping the last sleep to the remaining budget; a timeout is `null` — an outcome the caller reports as `WindowDetected:false`, never an exception
- `WindowGeometry.Validate(x, y, width, height)` / `Apply(match, ..., restoreFirst, native)` — B-9's move/resize: at least one of the four is required and a given width/height must be positive (checked before any window is read); a minimised (`IsIconic`) or maximised (`IsZoomed`) target is an `InvalidOperationException` naming the state unless `restoreFirst` sends `SW_RESTORE` first; one `SetWindowPos` always carries `SWP_NOZORDER|SWP_NOACTIVATE` (a move must not raise or focus the window) plus `SWP_NOMOVE` when no position was asked for and `SWP_NOSIZE` when no size was, a half-given pair being filled from the current rect; `After` is a second `GetWindowRect`, never the requested rectangle (roadmap C11)
- `TypePlanner.Plan(text, options)` — B-1's whole typing decision as an ordered list of `TypeStep`s (`shortcut` / `key` / `text` / `paste`) plus the `Method` the result reports: `Clear` first (`ctrl+a`, `backspace`), then `Caret` (`ctrl+home` / `ctrl+end`; `Idle` emits nothing), then the text — one `paste` step when it is at least `PasteThreshold` (200) characters **and** holds no control character other than `\n`/`\t`, otherwise literal chunks with every LF, CR or CRLF as an `enter` key and every tab as a `tab` key (a CRLF is one break) — then `enter` when `PressEnter`. A negative `PaceMs` is an `ArgumentException` raised before any step exists
- `DragPath.Points(from, to, steps, nudge)` — B-2's pointer path: the first point is a `nudge`-long step along the travel direction (skipped when `nudge` is 0 or the drag is shorter than the nudge, in which case the origin is emitted instead), then `steps` interpolated points, the last exactly `to`, so the list is always `steps + 1` long and never doubles back on either axis. A zero-distance drag is just the destination; `steps < 1` or a negative `nudge` is an `ArgumentOutOfRangeException`
- `BatchTargets.ParseTargets(json)` / `ParseEntries(json)` — B-7's one parser for `multi_select`'s `targets_json` and `multi_edit`'s `entries_json`: a JSON array of objects, or a JSON *string* holding that array (unwrapped once, the Claude Desktop quirk). Each entry is `{x,y}` **or** `{element_id}` — both, half a pair, or neither is an `ArgumentException`; `multi_edit` additionally requires `text` and reads the optional `clear` / `press_enter`. Every refusal names the parameter and the offending entry's index, and an empty array is refused too
- `ArgvJson.Parse(args_json)` — B-11: null or blank → null (the command keeps its whole-command-line meaning); a JSON array of strings → its items verbatim (an empty array still means argv mode); anything else — an object, a bare string, an array holding a non-string, unparseable text — is an `ArgumentException` naming `args_json` and the offending item
- `RegistryGuard.Refusal(path)` — C-2's guard on `registry_delete`'s key branch: the reason a key delete is refused, or null. An empty path (the hive root) and a short denylist of roots the profile or Windows itself depends on (`Software`, `Software\Classes`, `Software\Microsoft`, `Software\Microsoft\Windows[ NT][\CurrentVersion]`, `Software\Policies`, `Software\WOW6432Node`, `System`, `SYSTEM\CurrentControlSet`, `SAM`, `SECURITY`, `Environment`, `Control Panel`, `Volatile Environment`), compared after `Normalise` (trim, `/` → `\`, doubled separators collapsed, leading/trailing separators dropped) ordinal-ignore-case. Value deletes are not guarded, and the list guards the catastrophic roots only — `confirm` and the client's `destructiveHint` do the rest
- `LineWindow.Slice(text, offsetLines, limitLines)` — C-1's window behind `file_read`: lines split on `\n` with a trailing `\r` stripped (a CRLF file counts the same as an LF one) and a final newline adding no line; `offsetLines` is 1-based (0 and 1 are both the first line), `limitLines` 0 runs to the end, an offset past the end returns zero lines with `Truncated:false`, and the content joins the window with `\n`. A negative offset or limit is an `ArgumentException` naming the parameter
- `CpuSample.Percent(before, after, elapsed, cores)` / `SortAndLimit(rows, sortBy, limit)` — C-3's pure half of the process list: the percentage is `(after − before) / elapsed / cores × 100` clamped to 0–100 and rounded to one decimal, `0` when `elapsed ≤ 0`, `cores ≤ 0` or the delta is negative (a process that exited between the readings), so one saturated core of eight reads `12.5`. The sort is memory and CPU descending, `name` ordinal-ignore-case ascending, `pid` ascending, every key tie-broken by pid; `limit` 0 keeps every row
- `PathCanonical.Canonical(fullPath, native)` — C-1 round 4d's one spelling every alias shares, behind the `IFinalPathNative` seam so it is unit-tested with a fake volume: ask the volume for the path itself, then for each ancestor up to the root, take the first answer and append the segments below it as written; a path the volume cannot speak for at any level comes back as written, and the result never carries a trailing separator. A `subst` drive, a mapped network drive, a junction and a symlink all reduce to the same string, which is what `FileSystemService`'s containment and root checks compare
- `DomPage.Render(page)` / `Hint(scroll)` — C-5's renderer for `scrape(source:"dom")`: the page's visible text lines joined with `\n`, then a blank line and one scroll hint when the document scrolls vertically — `VerticalPercent <= 0` is "Reached top of the page; scroll down to see more.", `>= 100` "Reached bottom of the page; scroll up to see more.", anything between "Scrolled N% down the page; scroll up or down to see more." (N rounded away from zero). No scroll pattern, or no vertical scrolling, is no hint and no trailing blank line — the whole page is on screen
- `ScrapeSummary.SystemPrompt(query, truncated)` / `Request(content, query, truncated)` — C-5's sampling request, pure so the prompt is testable without a server or a model: strip navigation, headers, footers, cookie and consent banners, advertisements and repeated boilerplate; keep names, numbers, dates, prices and quoted values verbatim; invent nothing; answer `query` from the page (quoting the passages, saying plainly when the page does not answer it) or, without one, summarise the page faithfully; and, when `truncated`, say the text is only the beginning of a longer page and do not conclude the page lacks what may lie past the cut. One user message carries the already-capped content, `MaxTokens` is 2048
- `TextCap.Cut(text, maxChars, out truncated)` — C-5's one cut both `scrape` sources share, so `max_chars` means the same thing for a fetched page and a walked one; a cut that would land between the halves of a surrogate pair takes one character less
- `UiText.Sanitize` — strips Private Use Area code points, replaces lone UTF-16 surrogates with U+FFFD, drops C0/C1 controls except tab/LF/CR, trims; returns the same instance when nothing needed changing
- `WindowFilter` — A-1's judgement over the `WindowProbe` records `WindowService` gathers, so every rule is provable on hand-written probes with no desktop attached. `Keep` drops a window that is not visible, a `WS_EX_TOOLWINDOW` without `WS_EX_APPWINDOW`, a DWM-cloaked one (UWP ghosts, other virtual desktops), a zero-area one, the shell chrome classes (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `Progman`, `WorkerW`, `IME`, `MSCTFIME UI`), an untitled one unless `includeHidden` (the title is judged **after** `UiText.Sanitize`) and a minimized one unless `includeMinimized`; `StateOf` reads `Minimized` before `Maximized` (a minimized window keeps `WS_MAXIMIZE`); `IsBrowser` matches `chrome, msedge, firefox, brave, opera, vivaldi` with or without `.exe`, and `IsChromium` (C-5) the same set minus `firefox` — the browsers that expose their page to UI Automation as a `RootWebArea`, which is what `scrape(source:"dom")` needs; `Build` projects the survivors onto `WindowInfo`, renumbering `ZOrder` from 0 and taking `MonitorIndex` from the window's centre via `CursorMath.MonitorIndexOf`; `ActiveOf` picks the entry flagged `IsActive`, so `active` reports the list's real `ZOrder`

### Snapshot core (`Services/UiTree/`)

`internal` types the `snapshot` tool is built from, split out so everything except the traversal
itself is unit-tested headless:
- `UiNode` — the record of every fact one element contributes: control type, name, bounds, window
  title, depth, enabled, off-screen, focus, password, value, range min/value/max, toggle state,
  expand state, access key, accelerator key, legacy role, `ScrollInfo`
- `UiClassifier` — **the single home of D-6's interactive control-type set** (`UIAutomationService.InteractiveControlTypes` now forwards to it, so the find path and the snapshot cannot drift), plus upstream's LegacyIAccessible role fallback for `Custom` elements (`text` counts only when the node carries a value), the informative set, `ActionFor` (`Edit`/`Document` → fill, CheckBox → toggle, ComboBox → select, Slider/Spinner → slide, ScrollBar → scroll, else click), `IsScrollable`, `CenterOf`, `ShortcutOf`
- `ElementBudget` — `TryTake` per admitted node, `Truncated` on the first refusal, and one `NoteFor(limit)` sentence the renderer prints verbatim
- `UiTraverser` — walks one window: re-fetches the root under a single FlaUI `CacheRequest` (`TreeScope.Subtree`, `AutomationElementMode.Full`, with each pattern *property* id cached as well) and walks `CachedChildren`, so a subtree costs one cross-process fetch instead of one per property. Every read is guarded, names and values go through `UiText.Sanitize`, each node is clipped to the window rect by the pure `Clip` and dropped when off-screen or zero-area, and the budget is spent once per admitted node. Pre-order, root first
- `SnapshotRenderer` — the compact text form: cursor line, active window, z-ordered window list, interactive rows grouped by window in first-appearance order with a fixed tag order (action, focused, password, value, toggle, expand, shortcut, range), scrollable rows with percentages and `[reached top]`/`[reached bottom]`, then the `Pages` section when `use_dom` asked for one (per browser window: the document id, title, URL and `[v: N%]`, then its visible text lines — or just the note when there was no page), then the budget note when truncated, then a `Timing:` line when the server is profiling. A password never prints a value, values clip at 80 chars, and CR/LF/tab/backslash are escaped so one element is always one row
- `ElementTarget.CentreOf(ElementInfo)` — B-4 / roadmap C1: the one place an `el_N` id becomes the point an input verb aims at. Integer-division centre of the bounds (a negative coordinate is fine — that is a monitor left of or above the primary); an off-screen element, one with null bounds, or one with a zero/negative width or height is an `InvalidOperationException` naming the id, the name (clipped to 80 characters by `UiRefusals.Clip`) and the reason, off-screen reported first because both usually hold at once and it is the actionable one; since E-2 the off-screen refusal leads with `interact_element scroll_into_view` (then scrolling the container, or focusing the window)
- `DomCorrection` — A-5's pure core (upstream's `_dom_correction`), taking walk entries as `(node, parentIndex)` pairs so every rule is provable without a browser: `SuppressesInteractive` (the walk-root `Document` is the page, so it keeps its id and its scrollable row but is never an interactive control), `PageText` (the Names of the `Text` nodes in document order, minus one that merely repeats its interactive parent's Name and minus blanks), `PageFor` (entries → `SnapshotPage`, entry 0 being the document) and `NoPage`/`NoPageNote` for a browser window with no page document
- `WaitConditions.Evaluate(condition, text, evidence)` / `NameOf(condition)` — B-6's pure verdict on one poll (roadmap C10), with no UIA, no desktop and no clock: `ElementExists` takes the first match, `ElementEnabled` the first *enabled* match (a match that is disabled is reported as such), `FocusedElement` checks the snapshot's focused interactive element's name contains the text, `TextExists` scans the snapshot's element names, element values, scrollable names and — with `use_dom` — the `Pages` text, and `ActiveWindow` matches the active `WindowInfo`'s title exact → substring → fuzzy (`WindowMatcher.FuzzyThreshold`). Each verdict carries the one-line `Detail` the result reports; evidence a condition did not need, or a poll could not gather, is "not there yet", never a throw. `WaitEvidence` is the record one poll fills in
- `ElementCache<T>` / `ElementCache.CapacityFor(options)` — D-11's id store: `Add` mints `el_N` into a least-recently-used set bounded by the capacity (a hit refreshes recency), `AddSnapshot` mints outside it and `EvictSnapshot` drops every snapshot id at once, `Remove` drops one id of either kind, and `Get` throws the *expired* / *unknown* `KeyNotFoundException`. `WasIssued(id)` is answered from the monotonic counter alone (canonical `el_N` with N below it), so no history is kept and `el_00` or ` el_0` never alias `el_0`. The capacity saturates at `int.MaxValue`
- `UiRefusals` — D-10's element refusals built without a live element: `PatternNotSupported`, `Gone`, `ChildGone`, `Translate` (FlaUI's `ElementNotEnabledException`, `PatternNotSupportedException`, `PropertyNotSupportedException`, `MethodNotSupportedException`, `NotSupportedByFrameworkException`, element-gone faults and the UIA HRESULTs `UIA_E_ELEMENTNOTENABLED`/`UIA_E_NOTSUPPORTED` → `InvalidOperationException`; null for anything already caller-facing, a cancellation or an unrecognised fault), `ProbeSaysGone`, `NeedsProbe`, and E-2's `Clip` (a name or echoed value as a refusal quotes it: at most `MaxQuoted` = 80 characters, then `...`, never splitting a surrogate pair — not yet applied inside `PatternNotSupported`/`ChildGone`/`Translate`, parity D-27)
- `UiRoots.FirstAvailable(candidates…)` — D-10's root ladder: the first candidate that answers non-null without throwing; the last is the unconditional fallback, and a cancellation propagates

### `OcrService`

Uses the **Windows.Media.Ocr** WinRT API:
- Calls `OcrEngine.TryCreateFromUserProfileLanguages()` for language detection
- `ExtractTextAsync(region?)` returns the recognized text
- Captures through `IScreenshotService` with `MaxWidth`/`MaxHeight` of 0, so OCR always reads a full-resolution PNG whatever the `screenshot` defaults are

### `AudioService`

v0.2.0 limitation — uses PowerShell + `SendKeys` as a backend:
- `GetAsync()` — reads volume via `Get-AudioDevice` PowerShell module or WMI fallback
- `SetVolumeAsync()` — sends `VK_VOLUME_UP`/`VK_VOLUME_DOWN` key presses
- `SetMutedAsync()` — sends `VK_VOLUME_MUTE` toggle (cannot set absolute mute state)
- Tracked for v0.3.0 to switch to NAudio/CoreAudio COM for accurate read/write

### `NotificationService`

C-4 — toasts in-process, no PowerShell cold start and no serialization gate:
- The one WinRT call sits behind the internal `IToastSink.Show(appId, toastXml)` seam
  (`WinRtToastSink` in production — `ToastNotificationManager.CreateToastNotifier(appId).Show(...)`
  through the `net10.0-windows10.0.19041.0` projection — a fake in the unit tests)
- Windows treats the AppUserModelId as the toast's identity. Before the first show the service
  checks `HKCU\Software\Classes\AppUserModelId\<id>` through `IRegistryService.ListAsync` and, for
  **its own default id only**, writes `DisplayName` there when it is absent — once per process. A
  caller-supplied id is never written
- `Registered` in the result is what the platform will accept: a packaged AUMID (contains `!`), or
  an `AppUserModelId\<id>` key under HKCU or HKLM. It is reported, not enforced — the show is
  attempted either way
- A `COMException` of `0x80070490` (element not found — the platform has not picked the fresh
  registration up) is retried once after 1 s; a second failure returns `Shown:false` with a `Note`
  naming the id, the HResult and the registration requirement. Any other exception propagates

---

## NuGet Package Reference

| Package | Version | Purpose |
|---------|---------|---------|
| `ModelContextProtocol` | 2.2.0 | MCP SDK — stdio transport, `[McpServerTool]` discovery, request filters |
| `ModelContextProtocol.AspNetCore` | 2.2.0 (lockstep) | Streamable HTTP transport (`WithHttpTransport`, `MapMcp`) on Kestrel via the `Microsoft.AspNetCore.App` framework reference |
| `FlaUI.UIA3` | 5.0.0 | Windows UI Automation API (UIA3 COM wrapper) |
| `H.InputSimulator` | 1.* | `SendInput`-based keyboard and mouse simulation |
| `SkiaSharp` | 4.151.1 | Screenshot capture, image encode/decode |
| `Microsoft.Windows.CsWin32` | 0.3.* (`PrivateAssets=all`) | Source-generated P/Invoke for Win32 APIs (DPI, AUMID, `SetCursorPos`, `VkKeyScan`, etc.) |
| `Microsoft.Extensions.Hosting` | shared framework | Generic Host, DI container, configuration (no `PackageReference`; comes with the runtime / `Microsoft.AspNetCore.App`) |
| `ReverseMarkdown` | 6.2.1 | HTML → Markdown conversion for `Scrape` tool; its transitive `AngleSharp` parser also supplies the document title and the nesting-depth measurement `WebService` bounds the conversion with |
| `TaskScheduler` | 2.12.2 | Windows Task Scheduler COM automation |
| `TextCopy` | 6.* | Cross-platform clipboard read/write |
| `System.ServiceProcess.ServiceController` | 10.* | Windows service control (`service` tool) |
| `System.Diagnostics.EventLog` | shared framework | Windows Event Log querying (no `PackageReference`; provided by the `net10.0-windows` framework) |
| `System.Drawing.Common` | 10.* | GDI+ image support (legacy compat) |
| `System.Management` | 10.* | WMI query execution (`ManagementObjectSearcher`) |
