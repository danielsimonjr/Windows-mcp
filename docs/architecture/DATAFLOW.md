# Windows-MCP Data Flow

## Overview

This document describes the data flow patterns within Windows-MCP, illustrating how information moves through the system from MCP tool invocation to Windows OS interaction and back.

The key architectural shift from the Python version: there are no module-level globals. The MCP SDK's `WithToolsFromAssembly()` source generator handles dispatch, the DI container wires all dependencies, and every service call is async.

---

## Primary Data Flow: MCP Request Dispatch

How a tool call travels from the AI agent to the Windows API and back:

```
┌──────────┐   ┌───────────────────┐   ┌──────────────┐   ┌────────────────┐
│ AI Agent │   │  MCP SDK (stdio)  │   │  Tool Class  │   │    Service     │
│          │   │  StdioTransport   │   │ [McpServTool] │   │ Implementation │
└────┬─────┘   └────────┬──────────┘   └──────┬───────┘   └───────┬────────┘
     │                  │                      │                   │
     │  JSON-RPC        │                      │                   │
     │  {method,params} │                      │                   │
     ├─────────────────►│                      │                   │
     │                  │  Deserialize params  │                   │
     │                  │  Route to method     │                   │
     │                  ├─────────────────────►│                   │
     │                  │                      │  await ServiceAsync│
     │                  │                      ├──────────────────►│
     │                  │                      │                   │  Windows API
     │                  │                      │                   ├──────────►
     │                  │                      │                   │◄──────────
     │                  │                      │◄──────────────────┤
     │                  │                      │  JsonSerializer   │
     │                  │◄─────────────────────┤  .Serialize(result)
     │                  │  JSON-RPC response   │                   │
     │◄─────────────────┤                      │                   │
```

All tool methods follow the same shape:
```csharp
[McpServerTool, Description("...")]
public async Task<string> ToolName(/* parameters */)
{
    var result = await _service.DoSomethingAsync(/* mapped params */);
    return JsonSerializer.Serialize(result);  // or plain string
}
```

---

## GetState Data Flow (UI Automation)

`GetState` is the primary context-gathering tool — returns the full UI element tree of the foreground application.

### Sequence

```
┌──────────┐   ┌─────────────┐   ┌──────────────────────┐   ┌───────────────┐
│ AI Agent │   │UIAutoTools  │   │ UIAutomationService  │   │ FlaUI.UIA3    │
│          │   │             │   │                      │   │ (Windows UIA3)│
└────┬─────┘   └─────┬───────┘   └──────────┬───────────┘   └───────┬───────┘
     │               │                      │                       │
     │ GetState()    │                      │                       │
     ├──────────────►│                      │                       │
     │               │ GetStateAsync()      │                       │
     │               ├─────────────────────►│                       │
     │               │                      │ AutomationElement     │
     │               │                      │ .RootElement          │
     │               │                      ├──────────────────────►│
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │                      │ GetForegroundWindow() │
     │               │                      ├──────────────────────►│
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │                      │ TreeWalker.Walk()     │
     │               │                      ├──────────────────────►│
     │               │                      │  [recursive DFS]      │
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │    UiState           │                       │
     │               │◄─────────────────────┤                       │
     │               │ JsonSerializer       │                       │
     │ JSON string   │ .Serialize(state)    │                       │
     │◄──────────────┤                      │                       │
```

### Data Transformations

```
1. MCP Request
   └─► no parameters (returns foreground window state)

2. UIAutomationService.GetStateAsync()
   ├─► Get desktop root via AutomationElement.RootElement
   ├─► Identify foreground window via P/Invoke GetForegroundWindow()
   └─► Walk UIA3 tree recursively

3. Per-element classification (FlaUI ControlType checks):
   ├─► Interactive: Button, Edit, CheckBox, RadioButton, ComboBox,
   │               ListItem, MenuItem, Hyperlink, TabItem, TreeItem, ...
   ├─► Text: Text, Document controls (read-only content)
   └─► Scrollable: elements supporting IScrollPattern

4. UiState aggregate:
   UiState {
     Interactive: [{ Id, Name, ControlType, BoundingBox, Value, ... }]
     Text:        [{ Id, Name, Content }]
     Scrollable:  [{ Id, Name, BoundingBox, H: bool, V: bool }]
   }

5. MCP Response: JSON string of UiState
```

---

## Click Data Flow

```
┌──────────┐   ┌────────────┐   ┌──────────────────┐   ┌────────────────────┐
│ AI Agent │   │InputTools  │   │  InputService    │   │ H.InputSimulator   │
│          │   │            │   │                  │   │ (SendInput Win32)  │
└────┬─────┘   └─────┬──────┘   └────────┬─────────┘   └─────────┬──────────┘
     │               │                   │                        │
     │ Click(x,y,    │                   │                        │
     │  button,      │                   │                        │
     │  clicks)      │                   │                        │
     ├──────────────►│                   │                        │
     │               │ ParseButton(btn)  │                        │
     │               ├──────────────────►│                        │
     │               │  ClickAsync(x,y,  │                        │
     │               │  MouseButton,     │                        │
     │               │  clicks)          │                        │
     │               ├──────────────────►│                        │
     │               │                   │ MoveMouse(x, y)        │
     │               │                   ├───────────────────────►│
     │               │                   │                        │ SendInput(MOUSEMOVE)
     │               │                   │                        ├──────────────►
     │               │                   │ ButtonDown/Up × clicks │
     │               │                   ├───────────────────────►│
     │               │                   │                        │ SendInput(MOUSECLICK)
     │               │                   │◄───────────────────────┤
     │               │ ClickResult       │                        │
     │               │◄──────────────────┤                        │
     │ JSON string   │                   │                        │
     │◄──────────────┤                   │                        │
```

### Data

```
Input:  x=800, y=400, button="right", clicks=1

Processing:
  ParseButton("right") → MouseButton.Right
  InputService.ClickAsync(800, 400, MouseButton.Right, 1):
    ├─► IMouseSimulator.MoveTo(800, 400)    // absolute physical pixels
    └─► IMouseSimulator.RightButtonClick()  // SendInput(MOUSE_RIGHT_DOWN + MOUSE_RIGHT_UP)

Output: ClickResult { X=800, Y=400, Button="Right", Clicks=1 }
        → JSON: {"X":800,"Y":400,"Button":"Right","Clicks":1}
```

---

## Powershell Data Flow

```
┌──────────┐   ┌──────────┐   ┌──────────────────────┐   ┌───────────────────┐
│ AI Agent │   │ShellTools│   │  PowerShellService   │   │ System.Diagnostics│
│          │   │          │   │                      │   │    .Process       │
└────┬─────┘   └────┬─────┘   └──────────┬───────────┘   └─────────┬─────────┘
     │              │                    │                          │
     │ Powershell   │                    │                          │
     │ (command)    │                    │                          │
     ├─────────────►│                    │                          │
     │              │ RunAsync(command)  │                          │
     │              ├───────────────────►│                          │
     │              │                    │ [security filter]        │
     │              │                    │ ValidateCommand()        │
     │              │                    ├────────────────────────► │
     │              │                    │                          │
     │              │                    │ Process.Start()          │
     │              │                    │  powershell.exe          │
     │              │                    │  -EncodedCommand / -File │
     │              │                    ├─────────────────────────►│
     │              │                    │                          │ stdin closed
     │              │                    │◄─────────────────────────┤
     │              │                    │  (stdout, stderr,        │
     │              │                    │   exitCode)              │
     │              │ PowerShellResult   │                          │
     │              │◄───────────────────┤                          │
     │ JSON string  │                    │                          │
     │◄─────────────┤                    │                          │
```

### Data

```
Input:  command = "Get-Process | Select-Object Name,CPU | ConvertTo-Json"

Processing:
  1. ValidateCommand() — blocklist (IEX, Start-Process, Format-Volume, -EncodedCommand, download cradles)
  2. Process.Start(powershell.exe, -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand …)
     Oversize scripts fall back to a temp -File
  3. Stdin redirected and closed (child must not inherit MCP JSON-RPC)
  4. Await exit; read stdout + stderr; ParseErrors strips CLIXML progress scaffolding

Output: PSResult { Success, Stdout, Stderr, ExitCode, Errors }
        → JSON serialized by ShellTools (confirm:true required)
```

---

## Process Lineage / Orphan Data Flow

`Process` (actions `list|orphans|kill`) exposes recycle-aware parent lineage, orphan detection,
and root-grouping on top of the plain process list.

```
Flow — process action orphans / list includeLineage / list groupByRoot:

  AI Agent          →  ProcessTools.Process(action, name?, includeLineage?, groupByRoot?)
  ProcessTools      →  ProcessService.ListLineageAsync(...) | GroupByRootAsync(...)
  ProcessService    →  IWmiService.QueryAsync("Win32_Process", null, null)   [single bulk enumeration]
  ProcessService    :  Win32ProcRow.From(row) — parse raw CIM_DATETIME CreationDate → UTC at the seam
  ProcessService    →  ProcessLineage.Classify(rows, nowUtc)                 [pure, recycle-aware]
  ProcessService    :  apply orphansOnly / name-or-cmdline filter AFTER classification
  ProcessService    →  ProcessTools  →  AI Agent : JSON (ProcessLineageDto[] | ProcessGroupDto[])
```

### Data

```
Input:  orphans (name=null)  →  ListLineageAsync(orphansOnly:true, nameFilter:null)

Processing:
  1. QueryAsync("Win32_Process", null, null, ct) — single bulk WMI enumeration
  2. Parse each row's CreationDate (CIM_DATETIME string) at the seam only, to UTC DateTime
  3. Pure classifier over Win32ProcRow[] + nowUtc: for each process, resolve RootPid by walking
     parent links; orphaned = parent id absent, OR not provably recycled (a null CreationUtc on
     either side cannot prove recycling, so the parent is treated as alive); attach ageMinutes,
     runtimeKind, isSystemAdjacent
  4. Filter (orphansOnly / name substring on name-or-command-line) applied AFTER classification,
     so a filtered-out root PID still resolves correctly for surviving children

Output: ProcessLineageDto[] → JSON array (recycle-aware lineage + signals per process)
        or ProcessGroupDto[] → JSON array (processes collapsed under nearest-live root)

Kill-tree: KillTreeAsync(pid, expectedStartUtc?) verifies the root PID's start time once against
expectedStartUtc when given, then walks descendants leaves-first. Before killing each PID it
re-reads the live start time and compares it to that PID's snapshot CreationUtc, skipping any
mismatch — so a PID reused between the snapshot and the kill is not an innocent bystander (guards
PID reuse mid-walk). A snapshot row with no CIM date cannot be validated and is killed as-is.
```

---

## Screenshot + OCR Data Flow

```
┌──────────┐   ┌───────────┐   ┌──────────────────┐   ┌─────────────────────┐
│ AI Agent │   │ScreenTools│   │ScreenshotService │   │ SkiaSharp / GDI+    │
│          │   │           │   │   OcrService     │   │ Windows.Media.Ocr   │
└────┬─────┘   └─────┬─────┘   └────────┬─────────┘   └──────────┬──────────┘
     │               │                  │                         │
     │ Screenshot()  │                  │                         │
     ├──────────────►│                  │                         │
     │               │ CaptureAsync()   │                         │
     │               ├─────────────────►│                         │
     │               │                  │ BitBlt(screen)          │
     │               │                  ├────────────────────────►│
     │               │                  │◄────────────────────────┤
     │               │                  │ SKBitmap.Encode(PNG)    │
     │               │                  ├────────────────────────►│
     │               │ base64 PNG str   │◄────────────────────────┤
     │◄──────────────┤◄─────────────────┤                         │
     │               │                  │                         │
     │ Ocr(region)   │                  │                         │
     ├──────────────►│                  │                         │
     │               │ RecognizeAsync() │                         │
     │               ├─────────────────►│                         │
     │               │                  │ OcrEngine.RecognizeAsync│
     │               │                  ├────────────────────────►│
     │               │                  │  (Windows.Media.Ocr)    │
     │               │ OcrResult JSON   │◄────────────────────────┤
     │◄──────────────┤◄─────────────────┤                         │
```

---

## WaitFor Data Flow (Polling Loop)

`WaitFor` is the only tool with internal retry logic — all other tools are single-pass:

```
UIAutomationTools.WaitFor(text, timeout_ms, interval_ms)
        │
        ▼
UIAutomationService.WaitForAsync(text, timeout_ms, interval_ms)
        │
        ▼
  ┌─────────────────────────────────────────────────┐
  │ start = DateTime.UtcNow                         │
  │                                                 │
  │  ┌─────────────────────────────┐                │
  │  │ FindElementAsync(text, Any) │◄───────────┐   │
  │  └──────────────┬──────────────┘            │   │
  │                 │                           │   │
  │         found? ─┤                           │   │
  │           YES   │    NO                     │   │
  │           ▼     │    ▼                      │   │
  │       return    │  elapsed > timeout_ms?    │   │
  │       element   │    YES → return null      │   │
  │                 │    NO  → await Task.Delay │   │
  │                 │          (interval_ms) ───┘   │
  └─────────────────────────────────────────────────┘
```

---

## DI Resolution Flow at Startup

```
Host.CreateApplicationBuilder(args)
        │
        ▼
builder.Services.AddSingleton<IInputService, InputService>()
  ...  (24 services)
        │
        ▼
builder.Services.AddMcpServer(...)
    .WithStdioServerTransport()
    .WithToolsFromAssembly()      ← compile-time source generator
        │
        ▼
builder.Build()
        │
        ▼
  IServiceProvider built
  ┌─────────────────────────────────────────────────┐
  │  On first tool call, DI resolves:               │
  │                                                 │
  │  InputTools ← IInputService (InputService)      │
  │            ← IClipboardService (ClipboardSvc)  │
  │                                                 │
  │  UIAutomationTools ← IUIAutomationService       │
  │                      (UIAutomationService)      │
  │  ... etc.                                       │
  └─────────────────────────────────────────────────┘
        │
        ▼
builder.Build().RunAsync()
  → reads stdin forever (JSON-RPC)
  → dispatches to tool methods
  → exits when stdin closes (EOF)
```

---

## Element State Determination (FlaUI)

```
Is element interactive?
        │
        ▼
  ┌─────────────────────────┐
  │ ControlType in           │──NO──► Skip
  │ INTERACTIVE_CONTROL_TYPES│
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ IsEnabled == true        │──NO──► Skip
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ IsOffscreen == false     │──NO──► Skip
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ BoundingRectangle.Area  │──NO──► Skip
  │       > 0               │
  └──────────┬──────────────┘
             │YES
             ▼
      [Include in Interactive]


Interactive control types (FlaUI ControlType names):
  Button, Edit, CheckBox, RadioButton, ComboBox, List, ListItem,
  MenuItem, Hyperlink, SplitButton, TabItem, TreeItem, DataItem,
  Slider, Spinner, ScrollBar, Document
```

---

## AssertElement Data Flow

```
AssertElement(element_id, state)
        │
        ▼
UIAutomationService.AssertElementAsync(element_id, state)
        │
        ▼
  Resolve element by ID from internal cache
        │
        ▼
  switch (state)
  ├─ "exists"  → element != null
  ├─ "enabled" → element.IsEnabled
  ├─ "checked" → element.ToggleState == ToggleState.On
  ├─ "value"   → element.Value != null && element.Value != ""
  ├─ "visible" → !element.IsOffscreen
  └─ "focused" → element == AutomationElement.FocusedElement
        │
        ▼
  return true/false
        │
        ▼
  Tool: "PASS" or "FAIL: {state}"
```

---

## Error Handling Flow

### PowerShell Execution Errors

```
PowerShellService.RunAsync(command)
        │
        ▼
  ValidateCommand(command)  →  ArgumentException if blocked
        │ (passes)
        ▼
  Process.Start(...)
        │
  ┌─────┴─────┐
  ▼           ▼
Success     Exception
  │           │
  ▼           ▼
PowerShell  Return PowerShellResult{
Result       Stdout="", Stderr=ex.Message, ExitCode=-1}
```

### UI Automation Errors

```
UIAutomationService methods
  catch (Exception ex)
  └─► Return null or empty result (callers check for null)
  
WaitFor — timeout path:
  elapsed > timeout_ms → return null
  Tool returns "null" string (agent detects no match)
```

---

## Response Format

### Tool Response Shape

All tool methods return `Task<string>` where the string is either:
- **JSON** — from `JsonSerializer.Serialize(result)`
- **Plain string** — for simple acknowledgements (`"pressed ctrl+c"`, `"PASS"`, `"null"`)

### JSON Response Examples

```jsonc
// Click response
{"X":800,"Y":400,"Button":"Left","Clicks":1}

// PowerShell response
{"Stdout":"ProcessName  CPU\n---\npwsh   1.23\n","Stderr":"","ExitCode":0}

// GetState response (abbreviated)
{
  "Interactive": [
    { "Id": "1", "Name": "OK", "ControlType": "Button",
      "BoundingBox": {"Left":100,"Top":200,"Right":180,"Bottom":230} }
  ],
  "Text": [{ "Id": "2", "Name": "Save changes?", "Content": "Save changes?" }],
  "Scrollable": []
}
```

---

## Timing and Delays

| Location | Behavior | Notes |
|----------|----------|-------|
| `WaitFor` | Polls every `interval_ms` (default 500ms) up to `timeout_ms` (default 10s) | Only tool with a loop |
| `InputService` click | `Thread.Sleep` between multi-clicks | Prevents double-click collapse |
| `InputService` type | Delay between keystrokes | Simulates human typing cadence |
| `PowerShellService` | Async wait on process exit | No timeout enforced in v0.2.0 |
| MCP SDK stdio | No artificial pauses — reads JSON-RPC frames continuously | Contrast: Python `pg.PAUSE = 1.0` |
