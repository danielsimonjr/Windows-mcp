# Windows-MCP System Architecture

## Architectural Overview

Windows-MCP follows a four-layer architecture built on .NET 10 with dependency injection throughout. The system is organized into: MCP Protocol, Tool, Service Abstraction, and Service Implementation layers — each with clearly defined responsibilities and interfaces.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        MCP Protocol Layer                                    │
│                    (ModelContextProtocol SDK)                                │
│  Stdio / Streamable HTTP ◄──► JSON-RPC ──► WithToolsFromAssembly() discovery │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                           Tool Layer                                         │
│                 (19 [McpServerToolType] classes, 69 tools)                   │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐  │
│  │InputTools  │ │UIAutoTools │ │ FileTools  │ │SystemTools │ │WindowTools │  │
│  │ 11 tools   │ │  9 tools   │ │  9 tools   │ │  9 tools   │ │  5 tools   │  │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ └────────────┘  │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐  │
│  │ProcessTools│ │ScreenTools │ │  WebTools  │ │RegistryTls │ │NetworkTls  │  │
│  │  6 tools   │ │  2 tools   │ │  2 tools   │ │  3 tools   │ │  2 tools   │  │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ └────────────┘  │
│  ShellTools(1) · JobTools(1) · DiskTools(1) · StorageTools(1)                │
│  SecurityTools(3) · StartupTools(1) · IntegrityTools(1)                      │
│  UsnTools(1) · WatchTools(1)                                                 │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │ constructor injection
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                      Service Abstraction Layer                               │
│                    (WindowsMcp.Abstractions assembly)                        │
│  IInputService · IScreenshotService · IOcrService · IClipboardService        │
│  IAudioService · IPowerShellService · IUIAutomationService · IFileSystemSvc  │
│  IRegistryService · IServiceControlService · IEventLogService                │
│  ITaskSchedulerService · IProcessService · IWindowService · IWmiService      │
│  IEnvService · IPowerService · INotificationService · INetworkService        │
│  IWebService · IVirtualDesktopService · IFlashOverlay · IControlIndicator    │
│                                                 (40 interfaces total)        │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │ implemented by
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    Service Implementation Layer                              │
│                     (WindowsMcp.Services namespace)                          │
│  InputService · ScreenshotService · OcrService · ClipboardService            │
│  AudioService · PowerShellService · UIAutomationService · FileSystemService  │
│  RegistryService · ServiceControlService · EventLogService                   │
│  TaskSchedulerService · ProcessService · WindowService · WmiService          │
│  EnvService · PowerService · NotificationService · NetworkService            │
│  WebService · VirtualDesktopService · FlashOverlay · ControlIndicator        │
│               (40 singletons — registered in Hosting/WindowsMcpHost)         │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                        Windows Platform Layer                                │
│  ┌──────────────┐ ┌───────────────────┐ ┌──────────────────────────────────┐ │
│  │  FlaUI.UIA3  │ │ H.InputSimulator  │ │ CsWin32 / Win32 APIs             │ │
│  │ (UI Automat.)│ │  (keyboard/mouse) │ │ DPI, WinRT, WMI, COM, P/Invoke   │ │
│  └──────────────┘ └───────────────────┘ └──────────────────────────────────┘ │
│  ┌──────────────┐ ┌───────────────────┐ ┌──────────────────────────────────┐ │
│  │   SkiaSharp  │ │  TaskScheduler    │ │ System.Management / EventLog     │ │
│  │  (images)    │ │     (COM)         │ │ (WMI, Windows event logs)        │ │
│  └──────────────┘ └───────────────────┘ └──────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Layer Descriptions

### 1. MCP Protocol Layer

The MCP SDK (`ModelContextProtocol.Server`) handles all protocol concerns:

- **Transport** — selected by `Hosting/ServerOptions` from the command line (`WINDOWSMCP_*` env fallbacks):
  - **stdio** (default, no arguments): `WithStdioServerTransport()` — JSON-RPC on stdin/stdout, for hosts that spawn the exe.
  - **Streamable HTTP** (`--transport http`): `WithHttpTransport(o => o.Stateless = true)` + `MapMcp("/mcp")` on Kestrel, built by `Hosting/WindowsMcpHost.BuildHttpApp`. `--port`/`--bind` choose the endpoint, `--cert-thumbprint` (resolved by `Hosting/CertificateLocator`) makes it HTTPS-only, and `--api-key` installs a constant-time bearer gate ahead of every route. Stateless so a restart stays invisible to clients — the one tool that would use a session, `scrape(summarize:true)`'s sampling request, is told so by the `TransportOptions` record and returns the page text with a note instead (C-5).
- **Tool Discovery**: `WithToolsFromAssembly()` — discovers all `[McpServerTool]` methods, registering them with their parameter schemas automatically
- **Server Info**: `ServerInfo = new() { Name = "Windows-mcp", Version = Program.ServerVersion }` — the version comes from `<Version>` in `Directory.Build.props`
- **Server Instructions (E-1)**: `ServerInstructions = ServerInstructions.Text` (`Hosting/ServerInstructions.cs`) — sent in the `initialize` result on both transports, so a client that never loads `skills/windows/SKILL.md` still gets the snapshot → act → confirm loop and the safety rails. At most 2 000 characters of printable ASCII with `\n` breaks; every sentence is a claim `ServerInstructionsTests` pins against the code (tool names, parameters, the never-ask list = every `Destructive` tool with no `confirm` parameter, the takeover note, what evicts snapshot ids)
- **Shared wiring**: `WindowsMcpHost.AddWindowsMcp(options)` holds the service registrations, server identity and instructions, caller-facing error filter and tool discovery, so both transports are configured identically; only the transport call differs.
- **Caller-facing errors**: the call-tool filter returns `ToolErrors.MessageFor(ex)` — the exception's own message, capped at 2 000 characters — for `ArgumentException`, `InvalidOperationException`, `KeyNotFoundException`, `IOException`, `UnauthorizedAccessException` and `TimeoutException` (`ToolErrors.IsCallerFacing`), the deliberate answers a caller must be able to read. Everything else keeps the SDK's `"An error occurred invoking '<tool>'."` masking, so a refusal a library or the OS raises in another type (FlaUI's exceptions, `Win32Exception`, `SecurityException`, an HRESULT) is translated where it is called (D-10: `UiRefusals`, `ProcessStartErrors`, `RegistryErrors`, `PowerService`), and `ToolErrorsTests` scans the server's IL for any construction that is neither caller-facing nor on its site-keyed `MaskedOnPurpose` list. See `DATAFLOW.md` → "Which exception the caller sees".
- **Control indicator (S-11)**: the same filter runs a *driving* tool — one of the 13 names in `Hosting/DrivingTools.Names` (`click`, `type`, `shortcut`, `focus`, `switch_to_window`, …) — between `IControlIndicator.BeginAction()` and `EndAction()`, and appends a pending takeover to the next result of **any** tool as one extra, last text block (`WindowsMcpHost.TakeoverNote`). It never refuses, delays or cancels a call, and a fault in the indicator is swallowed rather than failing the tool. A cancelled call, or a fault the SDK masks, leaves the takeover pending for the next result. See `DATAFLOW.md` → "Control Indicator Flow".

**Critical startup requirements** (handled in `Program.cs` before host build):
```csharp
// First thing in Main: fill in a host-stripped environment (PATHEXT, ProgramData, ...) so every
// child process we spawn inherits a usable one. Host-set values are never overwritten, except
// PATHEXT and — C-6 — a Path with no System32 entry, which the registry's is appended to.
EnvironmentRepair.Apply();

// stdio mode only: prevent JSON-RPC response buffering on Windows (cp1252 default encoding)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// Both modes: Per-Monitor DPI Awareness V2 — physical pixel coordinates on multi-monitor
PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((nint)(-4)));
```

---

### 2. Tool Layer

Tool classes are `[McpServerToolType]`-annotated sealed classes that group related MCP tools. They receive services via constructor injection — they contain no business logic themselves, only parameter validation and delegation.

**Pattern:**
```csharp
[McpServerToolType]
public sealed class InputTools
{
    private readonly IInputService _input;
    private readonly IClipboardService _clipboard;
    private IUIAutomationService Uia { get; }        // B-4: turns element_id into a point

    public InputTools(IInputService input, IClipboardService clipboard, IUIAutomationService uia)
    {
        _input = input;
        _clipboard = clipboard;
        Uia = uia;
    }

    // C-7: every tool names all five annotation arguments, even where the value is the SDK default
    [McpServerTool(Title = "Click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Click at a point or on a snapshot element. Give x and y (physical virtual-desktop pixels …) or element_id (an el_N id from snapshot/find_element …). clicks: 1 single, 2 double, 3 triple, 0 = hover only. modifiers holds ctrl, shift and/or alt for the click … Returns {action: click|hover, x, y, button, clicks, elementId?, name?, modifiers?}.")]
    public async Task<string> Click(int? x = null, int? y = null, string? element_id = null,
                                    string button = "left", int clicks = 1,
                                    string? modifiers = null, CancellationToken ct = default)
    {
        var held = ParseModifiers(modifiers);   // E-3: refused before the target is resolved
        var target = await ResolveTargetAsync(x, y, element_id, allowCursor: false);
        // … HoverAsync when clicks == 0, else ClickAsync at target.X/target.Y —
        //   with modifiers: HoverAsync, then HoldingAsync(held, ClickAsync) releases them in reverse
    }
}
```

**Tool class inventory:**

| Tool Class | Tools | Services Injected |
|------------|-------|------------------|
| `InputTools` | 11 | `IInputService`, `IClipboardService`, `IUIAutomationService` (B-4: resolves `element_id` to a point) |
| `UIAutomationTools` | 9 | `IUIAutomationService` |
| `FileTools` | 9 | `IFileSystemService`, `IInputService`, `IFileStreamService` |
| `SystemTools` | 9 | `IWmiService`, `IEnvService`, `IPowerService`, `INotificationService`, `IAudioService`, `ISecurityService`, `IReliabilityService`, `IDriverService` |
| `WindowTools` | 5 | `IWindowService`, `IVirtualDesktopService` |
| `ProcessTools` | 6 | `IProcessService`, `IServiceControlService`, `ITaskSchedulerService`, `IEventLogService` |
| `ScreenTools` | 2 | `IScreenshotService`, `IOcrService`, `IWindowService`, `IInputService`, `IUIAutomationService`, `IFlashOverlay` (+ the `ScreenshotOptions` record) |
| `WebTools` | 2 | `IWebService`, `IUIAutomationService` (C-5: the `source:"dom"` page walk), `IWindowService` (the frontmost Chromium window) (+ the `TransportOptions` record) |
| `RegistryTools` | 3 | `IRegistryService` |
| `NetworkTools` | 2 | `INetworkService`, `IFirewallService` |
| `ShellTools` | 1 | `IPowerShellService`, `IJobService` |
| `JobTools` | 1 | `IJobService` |
| `DiskTools` | 1 | `IDiskService` |
| `StorageTools` | 1 | `IStorageService` |
| `SecurityTools` | 3 | `IAuthenticodeInspector`, `ISecurityService`, `ICertStoreService` |
| `StartupTools` | 1 | `IStartupReportService` |
| `IntegrityTools` | 1 | `IIntegrityService` |
| `UsnTools` | 1 | `IUsnService` |
| `WatchTools` | 1 | `IWatchService` |

---

### 3. Service Abstraction Layer (`WindowsMcp.Abstractions`)

A separate assembly (`WindowsMcp.Abstractions.csproj`) containing:
- **40 `IXxxService` interfaces** — define the contract for each domain
- **Model DTOs** in `WindowsMcp.Abstractions.Models` — records/classes shared between tools and services

The abstraction layer exists so tool classes compile against interfaces, not concrete types. This enforces the dependency inversion principle and makes services independently testable.

**Example interface:**
```csharp
public interface IInputService
{
    Task<ClickResult> ClickAsync(int x, int y, MouseButton button, int clicks);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button,
                               int durationMs, int steps);          // B-2: nudge + interpolated moves
    Task HoverAsync(int x, int y, int durationMs);
    Task<TypeResult> TypeAsync(string text);
    Task<TypeResult> TypeAsync(string text, TypeOptions options);   // B-1: clear/caret/enter, keys or paste
    Task PressKeyAsync(string key);
    Task PressShortcutAsync(string shortcut);
    Task ScrollAsync(int x, int y, string direction, int amount);
    Task ScrollAsync(int x, int y, string direction, int amount, bool shiftWheel);   // B-3
    Task KeyDownAsync(string key);                                  // B-7: hold a modifier (E-3: click modifiers, key hold_ms)
    Task KeyUpAsync(string key);                                    // B-7: release it
    Task<CursorPosition> GetCursorPositionAsync();
}
```

---

### 4. Service Implementation Layer

All 40 services are registered as **singletons** in `Hosting/WindowsMcpHost.AddWindowsMcp(ServerOptions)`, which both transports call; the parsed options enter the container alongside them as four options records — `ScreenshotOptions` (read by the screen tools), `UiTreeOptions` (injected into `UIAutomationService`), `TransportOptions` (read by `WebTools`, which cannot sample the client over the stateless HTTP transport) and `ControlIndicatorOptions` (S-11, injected into `ControlIndicator`):

```csharp
// --screenshot-scale, --flash, --profile-snapshot, --screenshot-backend
services.AddSingleton(new ScreenshotOptions(options.ScreenshotScale, options.Flash,
    options.ProfileSnapshot, options.ScreenshotBackend));
// --max-tree-elements, --profile-snapshot
services.AddSingleton(new UiTreeOptions(options.MaxTreeElements, options.ProfileSnapshot));
// C-5: --transport http is stateless, so a sampling request has no session to travel on
services.AddSingleton(new TransportOptions(Stateless: options.Transport == TransportKind.Http));
services.AddSingleton<IFlashOverlay, FlashOverlay>();   // always registered; the tool gates on ScreenshotOptions.Flash
// S-11: --control-indicator, --control-hotkey; always registered, nothing created until the first driving call
services.AddSingleton(new ControlIndicatorOptions(options.ControlIndicator, options.ControlHotkey,
    ControlIndicatorOptions.Default.Linger));
services.AddSingleton<IControlIndicator, ControlIndicator>();
services.AddSingleton<IInputService, InputService>();
services.AddSingleton<IScreenshotService, ScreenshotService>();   // takes IControlIndicator (optional ctor arg)
// ... (40 registrations)
```

Services contain all business logic and directly call Windows APIs through platform packages. They are constructed once at host startup and shared across all tool invocations.

---

### 5. Windows Platform Layer

| Package | Windows API | What It Does |
|---------|-------------|-------------|
| `FlaUI.UIA3` | UI Automation COM | Walk the accessibility tree, find/inspect/interact with elements |
| `H.InputSimulator` | `SendInput` Win32 | Inject keyboard and mouse events at driver level |
| `SkiaSharp` | GDI+/DirectX | Hold either capture backend's frame, downscale (Mitchell cubic), annotate, encode PNG/JPEG |
| `Windows.Graphics.Capture` (no package — the `net10.0-windows10.0.19041` WinRT projection) | WGC + D3D11 | The `wgc` screenshot backend: compositor frames for the GPU-accelerated and DRM surfaces GDI returns black for |
| `Windows.Management.Deployment` (no package — the same WinRT projection) | PackageManager | B-8's app catalog: `FindPackagesForUser("")` → `Package.GetAppListEntriesAsync()` for every packaged app's display name and AUMID |
| `CsWin32` | P/Invoke gen | Auto-generates interop for `SetProcessDpiAwareness`, `SetCurrentProcessExplicitAppUserModelID`, etc. |
| `TaskScheduler` | Task Scheduler COM | Create, read, update, delete scheduled tasks |
| `System.Management` | WMI | Query hardware, driver, and configuration data |
| `System.Diagnostics.EventLog` | Event Log API | Read Windows event log entries |
| `TextCopy` | Clipboard API | Cross-platform clipboard read/write |

---

## Design Patterns

### 1. Dependency Injection via `Microsoft.Extensions.Hosting`

All services follow the DI pattern — no static state, no singletons instantiated outside the container:

```csharp
// Hosting/WindowsMcpHost.cs — shared by both transports
public static IMcpServerBuilder AddWindowsMcp(this IHostApplicationBuilder builder, ServerOptions options)
{
    builder.Services.AddSingleton(new ScreenshotOptions(options.ScreenshotScale, options.Flash,
        options.ProfileSnapshot, options.ScreenshotBackend));
    builder.Services.AddSingleton<IInputService, InputService>();
    // ...
    return builder.Services.AddMcpServer(...).WithRequestFilters(...).WithToolsFromAssembly(...);
}

// Program.cs — stdio
var builder = Host.CreateApplicationBuilder(args);
builder.AddWindowsMcp(options).WithStdioServerTransport();
await builder.Build().RunAsync();

// Program.cs — HTTP (WindowsMcpHost.BuildHttpApp)
var web = WebApplication.CreateBuilder();
web.AddWindowsMcp(options).WithHttpTransport(o => o.Stateless = true);
app.MapMcp("/mcp");
```

### 2. Interface Segregation

Each service interface covers exactly one domain. Tool classes declare only the interfaces they actually use:

```csharp
// InputTools only needs input + clipboard + the element lookup — not screenshot, not filesystem
public InputTools(IInputService input, IClipboardService clipboard, IUIAutomationService uia)
```

### 3. Source-Generated Tool Discovery

`WithToolsFromAssembly()` uses a Roslyn source generator that runs at compile time. It emits a registration method that lists all `[McpServerTool]` methods with their `[Description]`-derived JSON schemas. There is no runtime reflection and no decorator registration step.

### 4. Record-Based DTOs

Model types in `WindowsMcp.Abstractions.Models` use C# records for immutability:

```csharp
public record AudioState(int Level, bool Muted);
public record ClickResult(int X, int Y, MouseButton Button, int Clicks);
```

### 5. Async-First API Surface

Every service method is `async Task<T>` or `async Task`. No blocking calls on tool dispatch threads.

---

## Project Structure

```
Windows-mcp.slnx
├── src/
│   ├── WindowsMcp/                        ← Main project
│   │   ├── WindowsMcp.csproj              (targets net10.0-windows10.0.19041)
│   │   ├── Program.cs                     (entry: env repair, AUMID + DPI setup, parse options, pick transport)
│   │   ├── Hosting/                       (ServerOptions, WindowsMcpHost, CertificateLocator,
│   │   │                                   EnvironmentRepair + PathMerge — C-6's pure Path merge;
│   │   │                                   HotkeyParser and DrivingTools — S-11's --control-hotkey
│   │   │                                   parser and the driving-tool names;
│   │   │                                   ServerInstructions — E-1's initialize text)
│   │   ├── Tools/                         (19 tool classes)
│   │   │   ├── InputTools.cs
│   │   │   ├── UIAutomationTools.cs
│   │   │   ├── FileTools.cs
│   │   │   ├── SystemTools.cs
│   │   │   ├── SecurityTools.cs
│   │   │   ├── WindowTools.cs
│   │   │   ├── ProcessTools.cs
│   │   │   ├── ScreenTools.cs
│   │   │   ├── ShellTools.cs
│   │   │   ├── JobTools.cs
│   │   │   ├── RegistryTools.cs
│   │   │   ├── NetworkTools.cs
│   │   │   ├── WebTools.cs
│   │   │   ├── DiskTools.cs
│   │   │   ├── StorageTools.cs
│   │   │   ├── StartupTools.cs
│   │   │   ├── IntegrityTools.cs
│   │   │   ├── UsnTools.cs
│   │   │   └── WatchTools.cs
│   │   ├── Services/                      (40 service implementations + helpers)
│   │   │   ├── InputService.cs            (+ TypePlanner, DragPath, IKeyboardSink and
│   │   │   │                               SimulatorKeyboardSink — B-1's typing plan and
│   │   │   │                               keystroke seam, B-2's drag path, B-7's KeyDown/KeyUp)
│   │   │   ├── BatchTargets.cs            (B-7: the pure parser behind multi_select/multi_edit)
│   │   │   ├── UIAutomationService.cs
│   │   │   ├── ScreenshotService.cs       (+ WgcCaptureBackend, Annotator, FlashOverlay, FlashGlow)
│   │   │   ├── ControlIndicator.cs        (+ ControlIndicatorState, ControlIndicatorArt — S-11's
│   │   │   │                               pure state and pixels; LayeredWindow — the overlay
│   │   │   │                               window both it and FlashOverlay paint through)
│   │   │   ├── UiTree/                    (snapshot core: UiNode, UiClassifier, ElementBudget,
│   │   │   │                               UiTraverser, SnapshotRenderer, DomCorrection,
│   │   │   │                               ElementTarget — B-4's element_id → point;
│   │   │   │                               WaitConditions — B-6's pure condition evaluator;
│   │   │   │                               ElementCache — D-11's bounded element-id cache;
│   │   │   │                               UiRefusals, UiRoots — D-10's element refusals
│   │   │   │                               and foreground-root ladder)
│   │   │   ├── WindowService.cs           (+ FuzzyMatch, WindowMatcher, ForegroundLadder and
│   │   │   │                               Win32ForegroundNative — B-10's matcher and ladder;
│   │   │   │                               WindowGeometry and Win32WindowGeometryNative —
│   │   │   │                               B-9's move/resize)
│   │   │   ├── AppCatalogService.cs       (+ AppCatalog, LaunchWait, IAppActivator and
│   │   │   │                               Win32AppActivator — B-8's catalog, window wait
│   │   │   │                               and activation)
│   │   │   ├── ProcessService.cs          (+ ArgvJson — B-11's args_json parser;
│   │   │   │                               CpuSample, IProcessWindowNative and
│   │   │   │                               Win32ProcessWindowNative — C-3's CPU sample,
│   │   │   │                               sort/limit and graceful-kill window seam;
│   │   │   │                               ProcessStartErrors — D-10's start-failure
│   │   │   │                               translation, shared with launch)
│   │   │   ├── FileSystemService.cs       (+ LineWindow — C-1's pure file_read window;
│   │   │   │                               PathCanonical, IFinalPathNative and
│   │   │   │                               Win32FinalPathNative — C-1 round 4's alias-proof
│   │   │   │                               containment check)
│   │   │   ├── RegistryService.cs         (+ RegistryGuard — C-2's pure root denylist;
│   │   │   │                               RegistryErrors — D-10's access-denied shape)
│   │   │   ├── PowerService.cs            (+ IPowerNative and Win32PowerNative — D-10's
│   │   │   │                               seam for the OS refusing a power action)
│   │   │   ├── NotificationService.cs     (+ IToastSink and WinRtToastSink — C-4's
│   │   │   │                               in-process WinRT toast seam)
│   │   │   ├── WebService.cs              (+ DomPage, ScrapeSummary, TextCap — C-5's pure
│   │   │   │                               page render, sampling prompt and text cut)
│   │   │   └── ...
│   │   └── Startup/                       (startup-report renderer + approval decoding)
│   └── WindowsMcp.Abstractions/           ← Contracts assembly
│       ├── WindowsMcp.Abstractions.csproj
│       ├── IInputService.cs
│       ├── IUIAutomationService.cs
│       ├── ... (40 interfaces)
│       └── Models/                        (24 DTO files)
│           ├── InputDtos.cs
│           └── ...
├── tests/WindowsMcp.Tests/                (xUnit + Moq + FluentAssertions)
└── docs/
    ├── architecture/
    └── upstream-parity-checklist.md
```

---

## Entry Point

```
dotnet run --project src/WindowsMcp
```

The `Program.cs` static `Main` returns `Task<int>`. It parses `ServerOptions` first (exit code 2 with usage on a bad option; `--help` prints usage). In **stdio** mode the host runs until the MCP client closes the stdin pipe (EOF), at which point `RunAsync()` returns and the process exits with code 0. In **HTTP** mode (`--transport http`) Kestrel runs until Ctrl+C / SIGTERM; startup is refused (exit 2) when binding off-loopback without an API key, or when the `--cert-thumbprint` certificate cannot be found or its private key opened.

---

## Security Considerations

1. **Transport exposure** — by default (stdio) no network port is opened; only the MCP client process can communicate. `--transport http` deliberately opens one, and every tool is reachable through it, so: the server refuses to start on a non-loopback bind without `--api-key`/`WINDOWSMCP_API_KEY` (constant-time bearer check applied to every path, 401 otherwise); `--cert-thumbprint` makes the port HTTPS-only; plain HTTP off-loopback is allowed but warned about at startup. Kestrel endpoints are configured explicitly, so `ASPNETCORE_URLS` cannot add an unauthenticated listener.
2. **PowerShell execution guards** — there is no command blocklist. `PowerShellService` serializes foreground calls through a gate, kills the process tree at the earlier of the caller's `timeout_seconds` (C-6, 1–900 s) and the 15-minute execution backstop, bounds each captured stream at 1 000 000 characters, redirects and closes stdin, and passes scripts whole via `-EncodedCommand`. Destructive *tools* are gated by `confirm:true` (README "Safety rails"); `scrape`/`http_request` fetch http/https only and reject private address ranges.
3. **DPI-aware coordinates** — `SetProcessDpiAwarenessContext` ensures coordinates are in physical pixels, preventing misclicks on HiDPI displays
4. **Concurrency** — services are singletons shared across concurrent tool calls; the ones that hold state (`UIAutomationService` STA queue, `PowerShellService` gate, `JobService`/`WatchService` registries) synchronize internally
