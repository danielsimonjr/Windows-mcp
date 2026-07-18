# Windows-MCP System Architecture

## Architectural Overview

Windows-MCP follows a four-layer architecture built on .NET 9 with dependency injection throughout. The system is organized into: MCP Protocol, Tool, Service Abstraction, and Service Implementation layers — each with clearly defined responsibilities and interfaces.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        MCP Protocol Layer                                    │
│                    (ModelContextProtocol SDK)                                │
│  StdioServerTransport ◄──► JSON-RPC ──► WithToolsFromAssembly() discovery   │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                           Tool Layer                                         │
│                 (18 [McpServerToolType] classes, 63 tools)                   │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ │
│  │InputTools  │ │UIAutoTools │ │ FileTools  │ │SystemTools │ │WindowTools │ │
│  │  8 tools   │ │  8 tools   │ │  9 tools   │ │  9 tools   │ │  5 tools   │ │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ └────────────┘ │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ │
│  │ProcessTools│ │ScreenTools │ │  WebTools  │ │RegistryTls │ │NetworkTls  │ │
│  │  6 tools   │ │  2 tools   │ │  2 tools   │ │  2 tools   │ │  2 tools   │ │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ └────────────┘ │
│   ┌─────────────┐ ┌────────────┐ ┌──────────────┐                           │
│   │ShellTools(1)│ │ DiskTools(1)│ │StorageTools(1)│                          │
│   └─────────────┘ └────────────┘ └──────────────┘                           │
│            ┌───────────────────┐                                            │
│            │ SecurityTools (3) │                                            │
│            └───────────────────┘                                            │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │ constructor injection
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                      Service Abstraction Layer                                │
│                    (WindowsMcp.Abstractions assembly)                        │
│  IInputService · IScreenshotService · IOcrService · IClipboardService       │
│  IAudioService · IPowerShellService · IUIAutomationService · IFileSystemSvc  │
│  IRegistryService · IServiceControlService · IEventLogService               │
│  ITaskSchedulerService · IProcessService · IWindowService · IWmiService     │
│  IEnvService · IPowerService · INotificationService · INetworkService       │
│  IWebService   (32 interfaces total)                                        │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │ implemented by
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    Service Implementation Layer                               │
│                     (WindowsMcp.Services namespace)                          │
│  InputService · ScreenshotService · OcrService · ClipboardService           │
│  AudioService · PowerShellService · UIAutomationService · FileSystemService  │
│  RegistryService · ServiceControlService · EventLogService                  │
│  TaskSchedulerService · ProcessService · WindowService · WmiService         │
│  EnvService · PowerService · NotificationService · NetworkService           │
│  WebService   (32 singletons — all registered in Program.cs via DI)         │
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

- **Transport**: `WithStdioServerTransport()` — reads JSON-RPC from stdin, writes to stdout
- **Tool Discovery**: `WithToolsFromAssembly()` — source generator discovers all `[McpServerTool]` methods at compile time, registering them with their parameter schemas automatically
- **Server Info**: `ServerInfo = new() { Name = "Windows-mcp", Version = "0.2.0" }`

**Critical startup requirements** (both handled in `Program.cs` before host build):
```csharp
// Prevent JSON-RPC response buffering on Windows (cp1252 default encoding)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// Per-Monitor DPI Awareness V2 — physical pixel coordinates on multi-monitor
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

    public InputTools(IInputService input, IClipboardService clipboard)
    {
        _input = input;
        _clipboard = clipboard;
    }

    [McpServerTool, Description("Click at screen coordinates.")]
    public async Task<string> Click(int x, int y, string button = "left", int clicks = 1)
        => JsonSerializer.Serialize(await _input.ClickAsync(x, y, ParseButton(button), clicks));
}
```

**Tool class inventory:**

| Tool Class | Tools | Services Injected |
|------------|-------|------------------|
| `InputTools` | 8 | `IInputService`, `IClipboardService` |
| `UIAutomationTools` | 8 | `IUIAutomationService` |
| `FileTools` | 9 | `IFileSystemService`, `IInputService`, `IFileStreamService` |
| `SystemTools` | 9 | `IWmiService`, `IEnvService`, `IPowerService`, `INotificationService`, `IAudioService`, `ISecurityService`, `IReliabilityService`, `IDriverService` |
| `WindowTools` | 5 | `IWindowService`, `IProcessService` |
| `ProcessTools` | 6 | `IProcessService`, `IServiceControlService`, `ITaskSchedulerService`, `IEventLogService` |
| `ScreenTools` | 2 | `IScreenshotService`, `IOcrService` |
| `WebTools` | 2 | `IWebService` |
| `RegistryTools` | 2 | `IRegistryService` |
| `NetworkTools` | 2 | `INetworkService`, `IFirewallService` |
| `ShellTools` | 1 | `IPowerShellService` |
| `DiskTools` | 1 | `IDiskService` |
| `StorageTools` | 1 | `IStorageService` |
| `SecurityTools` | 3 | `IAuthenticodeInspector`, `ISecurityService`, `ICertStoreService` |

---

### 3. Service Abstraction Layer (`WindowsMcp.Abstractions`)

A separate assembly (`WindowsMcp.Abstractions.csproj`) containing:
- **32 `IXxxService` interfaces** — define the contract for each domain
- **Model DTOs** in `WindowsMcp.Abstractions.Models` — records/classes shared between tools and services

The abstraction layer exists so tool classes compile against interfaces, not concrete types. This enforces the dependency inversion principle and makes services independently testable.

**Example interface:**
```csharp
public interface IInputService
{
    Task<ClickResult> ClickAsync(int x, int y, MouseButton button, int clicks);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button);
    Task HoverAsync(int x, int y, int durationMs);
    Task<TypeResult> TypeAsync(string text);
    Task PressKeyAsync(string key);
    Task PressShortcutAsync(string shortcut);
    Task ScrollAsync(int x, int y, string direction, int amount);
}
```

---

### 4. Service Implementation Layer

All 24 services are registered as **singletons** in `Program.cs`:

```csharp
builder.Services.AddSingleton<IInputService, InputService>();
builder.Services.AddSingleton<IScreenshotService, ScreenshotService>();
// ... (24 services total)
```

Services contain all business logic and directly call Windows APIs through platform packages. They are constructed once at host startup and shared across all tool invocations.

---

### 5. Windows Platform Layer

| Package | Windows API | What It Does |
|---------|-------------|-------------|
| `FlaUI.UIA3` | UI Automation COM | Walk the accessibility tree, find/inspect/interact with elements |
| `H.InputSimulator` | `SendInput` Win32 | Inject keyboard and mouse events at driver level |
| `SkiaSharp` | GDI+/DirectX | Capture screenshots, crop regions, encode PNG |
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
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IInputService, InputService>();
// ...
builder.Services.AddMcpServer(...).WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
```

### 2. Interface Segregation

Each service interface covers exactly one domain. Tool classes declare only the interfaces they actually use:

```csharp
// InputTools only needs input + clipboard — not screenshot, not filesystem
public InputTools(IInputService input, IClipboardService clipboard)
```

### 3. Source-Generated Tool Discovery

`WithToolsFromAssembly()` uses a Roslyn source generator that runs at compile time. It emits a registration method that lists all `[McpServerTool]` methods with their `[Description]`-derived JSON schemas. There is no runtime reflection and no decorator registration step.

### 4. Record-Based DTOs

Model types in `WindowsMcp.Abstractions.Models` use C# records for immutability:

```csharp
public record AudioState(int Level, bool Muted);
public record ClickResult(int X, int Y, string Button, int Clicks);
```

### 5. Async-First API Surface

Every service method is `async Task<T>` or `async Task`. No blocking calls on tool dispatch threads.

---

## Project Structure

```
Windows-mcp.sln
├── src/
│   ├── WindowsMcp/                        ← Main project
│   │   ├── WindowsMcp.csproj              (targets net9.0-windows10.0.22621)
│   │   ├── Program.cs                     (host + DI wiring)
│   │   ├── Tools/                         (15 tool classes)
│   │   │   ├── InputTools.cs
│   │   │   ├── UIAutomationTools.cs
│   │   │   ├── FileTools.cs
│   │   │   ├── SystemTools.cs
│   │   │   ├── WindowTools.cs
│   │   │   ├── ProcessTools.cs
│   │   │   ├── ScreenTools.cs
│   │   │   ├── ShellTools.cs
│   │   │   ├── RegistryTools.cs
│   │   │   ├── NetworkTools.cs
│   │   │   ├── WebTools.cs
│   │   │   ├── DiskTools.cs
│   │   │   ├── StorageTools.cs
│   │   │   └── StartupTools.cs
│   │   └── Services/                      (32 service implementations)
│   │       ├── InputService.cs
│   │       ├── UIAutomationService.cs
│   │       └── ...
│   └── WindowsMcp.Abstractions/           ← Contracts assembly
│       ├── WindowsMcp.Abstractions.csproj
│       ├── IInputService.cs
│       ├── IUIAutomationService.cs
│       ├── ... (20 interfaces)
│       └── Models/                        (10 DTO files)
│           ├── InputModels.cs
│           └── ...
└── docs/
    └── architecture/
```

---

## Entry Point

```
dotnet run --project src/WindowsMcp
```

The `Program.cs` static `Main` returns `Task<int>`. The host runs until the MCP client closes the stdin pipe (EOF), at which point `RunAsync()` returns and the process exits with code 0.

---

## Security Considerations

1. **Stdio-only transport** — no network port is opened; only the MCP client process can communicate
2. **PowerShell sandboxing** — `PowerShellService` filters dangerous commands and injection-risk flags before execution (blocklist in implementation)
3. **DPI-aware coordinates** — `SetProcessDpiAwarenessContext` ensures coordinates are in physical pixels, preventing misclicks on HiDPI displays
4. **Async-isolated services** — services are never shared across concurrent requests; the MCP SDK serializes tool calls
