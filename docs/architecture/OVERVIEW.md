# Windows-MCP Overview

## Introduction

Windows-MCP is a lightweight, open-source Model Context Protocol (MCP) server that enables AI agents to interact directly with the Windows operating system. Built on .NET 9 and C#, it exposes 63 MCP tools covering UI automation, file operations, process management, system monitoring, persistence/startup reporting, and more — all via the standard MCP stdio transport.

## Purpose

The primary goal of Windows-MCP is to provide AI agents with the ability to:

- **Understand Desktop Context**: Capture UI element trees from running applications via the Windows Accessibility API
- **Interact with UI Elements**: Click, type, scroll, drag, and manipulate interface elements programmatically
- **Control Windows**: Focus, resize, minimize, and manage application windows
- **Execute System Commands**: Run PowerShell commands for advanced system operations
- **Capture Screens**: Take screenshots and perform OCR on screen regions
- **Manage System Resources**: Control processes, registry, services, scheduled tasks, and event logs

## Key Features

| Feature | Description |
|---------|-------------|
| **Native Windows Integration** | Direct access to Windows UI Automation API via `FlaUI.UIA3` |
| **Dependency Injection** | All 35 services are singleton-scoped, wired via `Microsoft.Extensions.Hosting` |
| **Source-Generated Tool Discovery** | `[McpServerTool]` attributes are discovered at compile time by the MCP SDK source generator |
| **Interface-Driven Architecture** | Every service backed by an `IXxxService` interface in a separate Abstractions assembly |
| **DPI-Aware** | Per-Monitor DPI Awareness V2 enabled at startup for correct multi-monitor coordinate handling |
| **UTF-8 Stdio** | Output encoding forced to UTF-8 before host starts — prevents buffering bugs on Windows |
| **Tools-Only MCP Surface** | The server intentionally registers tools over stdio only; prompts/resources/completions are not exposed |

## Platform Requirements

- **Operating System**: Windows 10 or 11 (some features require Windows 10 1703+)
- **.NET Runtime**: .NET 9 or higher
- **Architecture**: x64 (64-bit)

## MCP 2.0 compliance surface

The repository verifies compatibility with the official `ModelContextProtocol` 2.2.0 SDK
and the MCP **2026-07-28** revision for the server features this project actually uses.

- **Transport:** stdio only
- **Capabilities advertised by this repo:** tools
- **Protocol flows covered by tests:** handshake, `ping`, `tools/list`, `tools/call`, and JSON-RPC unknown-method rejection
- **Deliberate non-goals:** prompts, resources, completions, roots, sampling, logging controls, HTTP transport

See `tests/WindowsMcp.Tests/Protocol/Mcp20ProtocolTests.cs`
for the repeatable conformance smoke suite.

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        AI Agent / LLM                           │
└─────────────────────────────────────────────────────────────────┘
                                │
                         MCP Protocol (stdio)
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│              Windows-MCP Server (Program.cs / Host)             │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │         ModelContextProtocol SDK (WithStdioServerTransport) ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │        MCP Tool Layer  (18 [McpServerToolType] classes)     ││
│  │   InputTools · UIAutomationTools · FileTools · ShellTools   ││
│  │   SystemTools · WindowTools · ProcessTools · ScreenTools    ││
│  │   NetworkTools · RegistryTools · WebTools · DiskTools       ││
│  │   StorageTools · SecurityTools · StartupTools               ││
│  │   IntegrityTools · WatchTools · UsnTools                    ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Abstraction Layer  (WindowsMcp.Abstractions)      ││
│  │        35 IXxxService interfaces + Model DTOs               ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Implementation Layer  (WindowsMcp.Services)       ││
│  │        35 XxxService singletons registered via DI           ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Windows Operating System                      │
│  ┌────────────────┐  ┌───────────────┐  ┌─────────────────────┐ │
│  │  FlaUI.UIA3    │  │H.InputSimulator│  │  CsWin32 / WinAPI   │ │
│  │ (UI Automation)│  │(keyboard/mouse)│  │   (DPI, WMI, etc.)  │ │
│  └────────────────┘  └───────────────┘  └─────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

## Available Tools

Windows-MCP exposes **63 MCP tools** across 18 tool classes:

### Input Tools (`InputTools` — 8 tools)
| Tool | Purpose |
|------|---------|
| `Click` | Click at screen coordinates (left/right/middle, single/double/triple) |
| `Drag` | Drag from one point to another |
| `Hover` | Hover cursor at coordinates with optional duration |
| `Type` | Type a string into the focused input |
| `Key` | Press a single key by name (Enter, Tab, F1-F12, arrows, etc.) |
| `Shortcut` | Press a keyboard shortcut (e.g., `ctrl+c`, `alt+tab`) |
| `Scroll` | Scroll the mouse wheel (up/down/left/right) |
| `Clipboard` | Get or set clipboard text |

### UI Automation Tools (`UIAutomationTools` — 8 tools)
| Tool | Purpose |
|------|---------|
| `GetState` | Capture full UI element tree of the foreground window |
| `FindElement` | Find a UI element by name, control type, or automation ID |
| `GetElement` | Get properties of a specific UI element |
| `InteractElement` | Invoke, toggle, select, or expand a UI element |
| `GetText` | Extract text content from a UI element |
| `GetTable` | Extract tabular data from a grid/table element |
| `AssertElement` | Assert element state with PASS/FAIL result |
| `WaitFor` | Wait until a condition on a UI element is met |

### Window Tools (`WindowTools` — 5 tools)
| Tool | Purpose |
|------|---------|
| `SwitchToWindow` | Focus a window by exact title |
| `Window` | minimize/maximize/restore/close a window by title |
| `MultiMonitor` | Get all monitor layouts and resolutions |
| `Launch` | Launch an application by name (`confirm:true`) |
| `Focus` | Alias for SwitchToWindow |

### File Tools (`FileTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `FileRead` | Read file contents |
| `FileWrite` | Write or append file contents |
| `FileManage` | Copy, move, delete, or create files/directories |
| `FileInfo` | Get file/directory metadata |
| `FileSearch` | Search for files by pattern |
| `FileHash` | Compute SHA256/SHA1/MD5 hex digest |
| `FileStreams` | NTFS alternate data streams + reparse target |
| `FileDialog` | Interact with open/save dialogs |
| `Archive` | Zip or unzip an archive (`confirm:true`) |

### System Tools (`SystemTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `SystemInfo` | WMI system info by category (os/memory/disk/gpu/battery) |
| `Audio` | Get/set volume or mute/unmute |
| `Notification` | Show a Windows toast notification |
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
| `Screenshot` | Capture a screenshot (full screen or region) |
| `Ocr` | Extract text from a screen region via OCR |

### Process Tools (`ProcessTools` — 6 tools)
| Tool | Purpose |
|------|---------|
| `Process` | List/inspect/kill processes: plain list, recycle-aware lineage + orphan detection (`orphans`), root-grouping, name/cmdline filtering, and recycle-safe kill by PID/name or whole tree |
| `ProcessInspect` | Deep per-process detail: parent PID, command line, start time, loaded modules |
| `StartProcess` | Start a detached process; returns the PID |
| `Service` | List/status/start/stop/restart Windows services |
| `ScheduledTask` | List/get/run/create/delete scheduled tasks |
| `EventLog` | Query the Windows Event Log |

### Shell Tool (`ShellTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Powershell` | Execute a PowerShell command; returns stdout, stderr, exit code |

### Registry Tools (`RegistryTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `RegistryGet` | Read a registry key or value |
| `RegistrySet` | Write a registry value |

### Network Tools (`NetworkTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Network` | adapters / ports / ping / dns / wifi |
| `Firewall` | list / add / remove firewall rules |

### Web Tool (`WebTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Scrape` | Fetch a public webpage and convert to Markdown (SSRF-protected) |
| `HttpRequest` | HTTP GET/POST/PUT/DELETE/PATCH/HEAD/OPTIONS (SSRF-protected) |

### Disk Tool (`DiskTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `DiskInspect` | usage / reclaimable / file_types / stale analysis |

### Storage Tool (`StorageTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `StorageHealth` | Diagnose disk/drive health: physical disks (model, bus/media type, SMART health + reliability counters), per-disk online/offline, volume→disk/partition map, and recent disk-stack error/warning events. Metadata-first + hang-safe; free space only when `include_usage:true` (time-boxed). |

### Startup Tools (`StartupTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `StartupReport` | HiJackThis-style boot/persistence report. COM-handler tasks resolve CLSID → InprocServer32. Summary ranks HIGH missing-target / persistence hooks, MEDIUM untrusted-third-party, LOW ms-file-missing. `format=summary` (default) \| `json` \| `text` \| `both`; `includeProcesses` opt-in |

### Integrity Tools (`IntegrityTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Integrity` | File-integrity tripwire: baseline / check / list |

### Watch Tools (`WatchTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Watch` | Live directory watching with server-side event buffer (max 16 sessions) |

### USN Tools (`UsnTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `FsChanges` | NTFS USN journal status / since (elevation required) |

## Core NuGet Dependencies

| Package | Purpose | Replaces (Python) |
|---------|---------|------------------|
| `ModelContextProtocol` | MCP server SDK, stdio transport | `fastmcp` |
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
