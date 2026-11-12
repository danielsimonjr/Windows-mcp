# Windows-MCP Overview

## Introduction

Windows-MCP is a lightweight, open-source Model Context Protocol (MCP) server that enables AI agents to interact directly with the Windows operating system. Built on .NET 10 and C#, it exposes 69 MCP tools covering UI automation, file operations, process management, system monitoring, persistence/startup reporting, and more — over the standard MCP stdio transport by default, or Streamable HTTP/HTTPS (`--transport http`) for clients on other machines.

## Purpose

The primary goal of Windows-MCP is to provide AI agents with the ability to:

- **Understand Desktop Context**: Capture UI element trees from running applications via the Windows Accessibility API
- **Interact with UI Elements**: Click, type, scroll, drag, and manipulate interface elements programmatically
- **Control Windows**: Enumerate the open windows and the foreground one, then focus, minimize, maximize, restore, or close them
- **Execute System Commands**: Run PowerShell commands for advanced system operations
- **Capture Screens**: Take screenshots the model sees inline (any monitor or region, auto-downscaled, cursor drawn, GDI or Windows.Graphics.Capture frames) and perform OCR on screen regions
- **Manage System Resources**: Control processes, registry, services, scheduled tasks, and event logs

## Key Features

| Feature | Description |
|---------|-------------|
| **Native Windows Integration** | Direct access to Windows UI Automation API via `FlaUI.UIA3` |
| **Dependency Injection** | All 40 services are singleton-scoped, registered in `Hosting/WindowsMcpHost.AddWindowsMcp` via `Microsoft.Extensions.Hosting` |
| **Source-Generated Tool Discovery** | `[McpServerTool]` attributes are discovered at compile time by the MCP SDK source generator |
| **Annotated Tools** | Every tool declares a title and all four MCP hints (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) so clients can auto-approve reads and confirm destructive calls |
| **Interface-Driven Architecture** | Every service backed by an `IXxxService` interface in a separate Abstractions assembly |
| **DPI-Aware** | Per-Monitor DPI Awareness V2 enabled at startup for correct multi-monitor coordinate handling |
| **UTF-8 Stdio** | Output encoding forced to UTF-8 before host starts — prevents buffering bugs on Windows |
| **Dual Transport** | stdio (default) or Streamable HTTP/HTTPS on a TCP port, gated by a bearer API key — opt-in via `--transport http` |
| **Server Instructions** | The `initialize` result carries a sub-2 000-character playbook (`Hosting/ServerInstructions.cs`) on both transports — the snapshot → act → confirm loop and the safety rails — so a client without the plugin skill still gets them |
| **Visible Control** | While a tool drives the mouse or keyboard, a person at the machine sees a ring around the pointer and a banner naming the takeover hotkey (default Ctrl+Alt+Shift+Backspace); pressing it hides them and the agent's next result says so. Never in a capture, never refuses or blocks anything; `--control-indicator off` turns it off |

## Platform Requirements

- **Operating System**: Windows 10 or 11 (some features require Windows 10 1703+)
- **.NET Runtime**: .NET 10 or higher
- **Architecture**: x64 (64-bit)

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        AI Agent / LLM                           │
└─────────────────────────────────────────────────────────────────┘
                                │
                  MCP Protocol (stdio | HTTP/HTTPS)
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│              Windows-MCP Server (Program.cs / Host)             │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │         ModelContextProtocol SDK (Stdio or Streamable-HTTP) ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │        MCP Tool Layer  (19 [McpServerToolType] classes)     ││
│  │   InputTools · UIAutomationTools · FileTools · ShellTools   ││
│  │   SystemTools · WindowTools · ProcessTools · ScreenTools    ││
│  │   NetworkTools · RegistryTools · WebTools · DiskTools       ││
│  │   StorageTools · StartupTools · SecurityTools · JobTools    ││
│  │   IntegrityTools · UsnTools · WatchTools                    ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Abstraction Layer  (WindowsMcp.Abstractions)      ││
│  │        40 IXxxService interfaces + Model DTOs               ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Implementation Layer  (WindowsMcp.Services)       ││
│  │        40 XxxService singletons registered via DI           ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Windows Operating System                      │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────────┐ │
│  │  FlaUI.UIA3    │  │H.InputSimulator│  │  CsWin32 / WinAPI  │ │
│  │ (UI Automation)│  │(keyboard/mouse)│  │  (DPI, WMI, etc.)  │ │
│  └────────────────┘  └────────────────┘  └────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

## Available Tools

Windows-MCP exposes **69 MCP tools** across 19 tool classes:

### Input Tools (`InputTools` — 11 tools)
| Tool | Purpose |
|------|---------|
| `Click` | Click at coordinates *or* on a snapshot element (`element_id` → its centre): left/right/middle, single/double/triple, `clicks:0` = hover only |
| `Drag` | Drag from a point, an element, or the current cursor to a point or an element — a nudge past the drag threshold then `steps` interpolated moves over `duration_ms` |
| `Hover` | Hover cursor at coordinates with optional duration |
| `Type` | Type into a target (`x`/`y`, `element_id`) or the focused input: optional `clear`, `caret` (start/end), `press_enter`; keys or a clipboard paste for long text |
| `Key` | Press one key: a character (`a`, `7`, `/`), `f1`-`f24`, or a name (enter, tab, esc, arrows, win, printscreen, …) |
| `Shortcut` | Press a chord (`ctrl+c`, `ctrl+shift+s`, `win+r`); a single key such as `win` also works |
| `Scroll` | Scroll the mouse wheel (up/down/left/right) at a point, an element's centre, or wherever the cursor is; `shift_wheel` for sideways scrolling with the vertical wheel |
| `Wait` | Pause for `seconds` (more than 0, at most 60) in-process, instead of a PowerShell sleep; returns `{"waited": seconds}` |
| `MultiSelect` | Click a JSON array of `{x,y}` / `{element_id}` targets in one call with Ctrl held for the whole batch (`ctrl:false` to click without it); every target is resolved before the first click, and the batch stops at the first failure reporting `failedIndex`/`error` with the results so far |
| `MultiEdit` | Click and type a JSON array of entries — a target plus `text` and the optional `clear`/`press_enter` — through the same path as `type`; same resolve-first and stop-at-first-failure rules, `method` per entry |
| `Clipboard` | Get or set clipboard text |

### UI Automation Tools (`UIAutomationTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `Snapshot` | One call for the whole desktop: window list, foreground window, cursor, every interactive element with its centre coordinates and an action hint, and the scrollable regions with their percentages; compact text by default, `format:"json"` for the DTOs (`include_tree` adds the element tree). `scope`: desktop / foreground / window; `max_elements` caps the walk; `use_dom:true` walks every Chromium browser window from its web page (the `RootWebArea` document) instead of the window and adds a `Pages` section with each page's id, title, URL, scroll percent and visible text |
| `GetState` | Capture the UI element tree of the foreground window (three levels deep, bounded by the element budget) |
| `FindElement` | Find elements whose name/value contains text (kind: any / interactive / text / scrollable) |
| `GetElement` | Get properties of a specific UI element by id, answered under that id |
| `InteractElement` | Act on a UI element by id: click / invoke / toggle / select / focus / type / expand / collapse / set_value / scroll_into_view, through the UIA pattern or a physical fallback; returns which one fired and what the control shows afterwards. An unsupported pattern, or a control that reports disabled, is refused naming the control — the disabled one before anything is touched (`scroll_into_view`, which moves only the view, is allowed on a disabled control) |
| `GetText` | Extract text content from a UI element |
| `GetTable` | Extract tabular data from a grid/table element |
| `AssertElement` | Assert element state (exists / enabled / checked / visible / focused / value with `expected`); `PASS` or `FAIL: <state> — observed <what was found>` |
| `WaitFor` | Poll until a condition holds: `element_exists` (default) / `element_enabled` (the same `find_element` filters) / `focused_element` / `text_exists` (anywhere in a snapshot of the scope, `use_dom:true` reading the browser page) / `active_window` (the foreground title, exact → substring → fuzzy 70+, no element walk); aliases `element\|enabled\|focused\|text\|window`. Always returns `{Satisfied, Condition, ElapsedMs, Attempts, Detail, Element?}` — a timeout is `Satisfied:false` with the last `Detail`, never an exception |

### Window Tools (`WindowTools` — 5 tools)
| Tool | Purpose |
|------|---------|
| `Window` | `list` the user-visible top-level windows in z-order (each with its `DesktopId`) or `active` the foreground one; `desktops` the virtual-desktop inventory and the current one; minimize, maximize, restore, or close a window named by `title` (exact → substring → fuzzy) or by `hwnd`, which wins; the result carries the matched title, hwnd, match strategy and score, and no match throws listing the open windows. `move` (x, y), `resize` (width, height) and `set_bounds` (all four) place a window with `SetWindowPos` and never raise or activate it; the target is matched the same way or is the foreground window when neither `title` nor `hwnd` is given, a minimized or maximized window is refused naming its state unless `restore_first:true`, and the result is `{Window, Before, After, MatchStrategy, Score, Restored}` with `After` re-read from the window |
| `SwitchToWindow` | Bring a window to the foreground by `title` (exact → substring → fuzzy, score ≥ 70) or `hwnd`; restores a minimised window, then climbs the SetForegroundWindow → AttachThreadInput → ALT-nudge ladder, re-reading `GetForegroundWindow` after each rung. Returns `{Window, MatchStrategy, Score, Restored, Strategy, Success}` |
| `Focus` | Alias of `SwitchToWindow` — same parameters, same result |
| `Launch` | Launch an app by its Start Menu name, a packaged app's display name, or a path: a path or an existing executable name starts outright, anything else resolves against the in-process app catalog (exact → prefix → fuzzy 70+). `wait_for_window` (default true) polls the window inventory up to `timeout_ms` for the app's window. Returns `{MatchedName, Kind, Score, Pid, Hwnd, Title, WindowDetected, Strategy}` |
| `MultiMonitor` | Enumerate monitors: geometry, primary flag, and per-monitor `WorkArea`, `Orientation` (0/90/180/270), `EffectiveDpi` and `Scale` |

### File Tools (`FileTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `FileRead` | Read a file as text (`max_bytes`, `encoding`); `offset_lines`/`limit_lines` return a JSON line window instead (C-1) |
| `FileWrite` | Write text to a file (`confirm:true`), `append` to it, and create the parent directory unless `create_parents:false` |
| `FileManage` | Copy, move (`overwrite:true` replaces an existing destination — set aside first, removed only once the copy or move has landed, put back on any failure or cancel), delete (`confirm:true`, plus `recursive:true` for a non-empty directory; a junction is unlinked, a missing path says so), or list `{Entries:[{Path, Name, IsDirectory, Size, Modified, Hidden, IsLink}], Truncated, MaxEntries}` (`pattern`, `recursive`, `include_hidden`, `max_entries`). Volume roots, device paths and a destination inside its own source are refused before anything is touched |
| `FileInfo` | Get file/directory metadata |
| `FileSearch` | Search for files by pattern |
| `FileHash` | Compute SHA256/SHA1/MD5 hex digest |
| `FileStreams` | NTFS alternate data streams + reparse target |
| `FileDialog` | Interact with open/save dialogs |
| `Archive` | Zip a directory or unzip an archive |

### System Tools (`SystemTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `SystemInfo` | WMI system info by category (os/memory/disk/gpu/battery) |
| `Audio` | Get/set volume or mute/unmute |
| `Notification` | Show a Windows toast in-process (WinRT) under an `app_id` AUMID; the default id is registered under HKCU on first use |
| `SecurityAudit` | Firewall/Defender/UAC/BitLocker posture snapshot |
| `Reliability` | Crash minidumps + recent reliability failure records |
| `DriverList` | Installed PnP drivers with version/date/signer/signed-state (BYOVD surface) |
| `WmiQuery` | Execute WMI queries for system data |
| `Env` | Get, set, or list environment variables (secret-name redaction) |
| `PowerAction` | Shutdown, reboot, logoff, lock, sleep, hibernate |

### Security Tools (`SecurityTools` — 3 tools)
| Tool | Purpose |
|------|---------|
| `VerifySignature` | Catalog-aware Authenticode trust verdict for a file |
| `DefenderStatus` | Microsoft Defender posture (real-time/tamper protection, signature age, scans) |
| `CertStore` | Enumerate a cert store; flags self-signed (rogue-root) and expired certs |

### Screen Tools (`ScreenTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Screenshot` | Capture the primary display, other monitors (`display`) or a region; returns MCP image content plus metadata (captured rect, monitor inventory, cursor, coordinate scale, the `backend` that produced the frame, `flash` when the post-capture glow was shown, `stages` when profiling is on). `backend`: `auto` (default) / `gdi` / `wgc` — `wgc` reads the compositor's own frames, which show the GPU-accelerated and DRM surfaces `gdi` returns black. `annotate:true` also walks the desktop and returns labelled boxes on the picture with the matching `el_N` element list as a second text block; `grid_columns`/`grid_rows` overlay guide lines captioned with virtual-desktop coordinates |
| `Ocr` | Extract text from a screen region via OCR (same `region`/`display` selection, always full resolution) |

### Process Tools (`ProcessTools` — 6 tools)
| Tool | Purpose |
|------|---------|
| `Process` | List/inspect/kill processes: plain list with a `CpuPercent` column, `sort_by` and `limit` (C-3), recycle-aware lineage + orphan detection (`orphans`), root-grouping, name/cmdline filtering, and recycle-safe kill by PID/name or whole tree — `graceful:true` asks the pid's windows to close and waits `grace_ms` before forcing |
| `ProcessInspect` | Deep per-process detail: parent PID, command line, start time, loaded modules |
| `StartProcess` | Start a detached process from a command line, or an executable plus an `args_json` array passed verbatim, with an optional `cwd` and `use_shell_execute`; returns `{pid, executable, args, cwd}` |
| `Service` | List/status/start/stop/restart Windows services |
| `ScheduledTask` | List/get/run/create/delete scheduled tasks |
| `EventLog` | Query the Windows Event Log |

### Shell Tool (`ShellTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Powershell` | Execute a PowerShell command; returns stdout, stderr, exit code and the trimmed-character counts. Emits MCP progress heartbeats on long foreground calls; `timeout_seconds` (1–900) bounds one call — expiry kills the child tree and returns `TimedOut: true` with the partial stdout — and `background: true` starts a job (see `JobTools`) instead of waiting |

### Job Tool (`JobTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Job` | Manage background PowerShell jobs (`status`/`output`/`cancel`/`list`): jobs run concurrently outside the foreground PowerShell gate, with bounded output capture and a per-job backstop |

### Registry Tools (`RegistryTools` — 3 tools)
| Tool | Purpose |
|------|---------|
| `RegistryGet` | Read a named value, or the whole key when no name is given — `{Path, Values, SubKeys}` |
| `RegistrySet` | Write a value (String / DWord / QWord / Binary / MultiString / ExpandString); `confirm:true` |
| `RegistryDelete` | Delete a value, or the key itself (`recursive:true` when it has sub-keys); `confirm:true`, and the hive root and the profile/OS roots are refused |

### Network Tools (`NetworkTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Network` | Adapters, listening ports (with owning process), Wi-Fi, DNS lookup, ping |
| `Firewall` | List, add, or remove firewall rules (`confirm:true` for add/remove) |

### Web Tools (`WebTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Scrape` | Read a page as text → JSON `{Source, Url, Title, Chars, Truncated, Content, Summarized, Model, Note}`: `source:"http"` fetches the URL and converts the HTML to Markdown, `source:"dom"` reads the page already open in a Chromium browser with a scroll hint, `max_chars` caps the text, and `summarize:true` asks the client's own model through MCP sampling (http/https only, private address ranges rejected) |
| `HttpRequest` | HTTP request (GET/POST/PUT/DELETE/PATCH) with optional headers and body (http/https only, private address ranges rejected) |

### Disk Tool (`DiskTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `DiskInspect` | List drives with capacity, free space, file system |

### Storage Tool (`StorageTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `StorageHealth` | Diagnose disk/drive health: physical disks (model, bus/media type, SMART health + reliability counters), per-disk online/offline, volume→disk/partition map, and recent disk-stack error/warning events. Metadata-first + hang-safe; free space only when `include_usage:true` (time-boxed). |

### Startup Tools (`StartupTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `StartupReport` | HiJackThis-style boot/persistence report. Sections: Run/RunOnce (all hives incl. per-user SIDs, with enabled state), Startup folders, scheduled tasks, auto-start services, hosts, DNS, Winsock LSP, shell extensions, Control Panel applets (registry + `System32`/`SysWOW64` `*.cpl`), accessibility ATs, Image File Execution Options, Winlogon hooks, AppInit_DLLs, Active Setup, browser proxy, trusted-zone sites. Every file-backed entry has a catalog-aware code-signing trust flag. `format=summary` (default — counts + only flagged entries, inline) \| `json` \| `text` \| `both`; `includeProcesses` opt-in |

### Integrity Tool (`IntegrityTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Integrity` | File-integrity tripwire over a curated watch-list (hosts file, Startup folders, `~/.claude/settings.json`, `~/.gitconfig`, `C:\` governance files): `baseline` (SHA-256 snapshot to `%LOCALAPPDATA%\windows-mcp\integrity`), `check` (added / removed / modified vs. baseline), `list`; extra `paths` can be added |

### USN Tool (`UsnTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `FsChanges` | NTFS USN change journal: `status` (journal id + USN range) and `since` (change records from a USN forward). Requires elevation |

### Watch Tool (`WatchTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Watch` | Live directory watching via `FileSystemWatcher`: `start` (returns a session id), `poll` (drain buffered events), `stop`, `list`; events buffer in a bounded ring between polls |

## Core NuGet Dependencies

| Package | Purpose | Replaces (Python) |
|---------|---------|------------------|
| `ModelContextProtocol` + `.AspNetCore` | MCP server SDK — stdio and Streamable HTTP transports | `fastmcp` |
| `FlaUI.UIA3` | Windows UI Automation API | `uiautomation` |
| `H.InputSimulator` | Keyboard and mouse simulation | `pyautogui` + `humancursor` |
| `SkiaSharp` | Image capture and processing | `Pillow` |
| `CsWin32` | P/Invoke code generation for Win32 APIs | `ctypes` |
| `Microsoft.Extensions.Hosting` | DI container and application host | N/A |
| `ReverseMarkdown` | HTML → Markdown conversion | `markdownify` |
| `TaskScheduler` | Windows Task Scheduler COM wrapper | N/A |
| `TextCopy` | Clipboard access | `pyperclip` |

## Use Cases

1. **Desktop Automation**: Automate repetitive Windows tasks via AI
2. **UI Testing**: AI-driven UI verification and regression testing
3. **Accessibility Analysis**: Extract UI element trees for accessibility auditing
4. **AI Agent Development**: Enable LLM agents to fully control Windows applications
5. **RPA (Robotic Process Automation)**: Business process automation through AI
6. **System Administration**: AI-assisted process, service, and registry management
