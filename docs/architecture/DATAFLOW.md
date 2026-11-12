# Windows-MCP Data Flow

## Overview

This document describes the data flow patterns within Windows-MCP, illustrating how information moves through the system from MCP tool invocation to Windows OS interaction and back.

The key architectural shift from the Python version: there are no module-level globals. The MCP SDK's `WithToolsFromAssembly()` source generator handles dispatch, the DI container wires all dependencies, and every service call is async.

---

## Primary Data Flow: MCP Request Dispatch

How a tool call travels from the AI agent to the Windows API and back:

```
┌──────────┐   ┌───────────────────┐   ┌──────────────┐   ┌────────────────┐
│ AI Agent │   │ MCP SDK transport │   │  Tool Class  │   │    Service     │
│          │   │  stdio or HTTP    │   │ [McpServTool] │   │ Implementation │
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

Between the transport and the tool method sits one call-tool filter
(`WindowsMcpHost.AddWindowsMcp`): it turns a caller-facing exception into a readable error result
(see "Which exception the caller sees"), runs a driving tool inside the control indicator's
`BeginAction`/`EndAction` and appends a pending takeover note to the result (see "Control
Indicator Flow").

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

`GetState` returns the UI element tree of the foreground application, three levels deep. (`Snapshot`, below, is the whole-desktop context-gathering call.)

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
     │               │                      │ GetForegroundWindow() │
     │               │                      │ → FromHandle          │
     │               │                      ├──────────────────────►│
     │               │                      │◄──────────────────────┤
     │               │                      │  (else FocusedElement │
     │               │                      │   → GetDesktop)       │
     │               │                      │                       │
     │               │                      │ FindAllChildren()     │
     │               │                      ├──────────────────────►│
     │               │                      │  [recursive DFS,      │
     │               │                      │   depth 3, budgeted]  │
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │    ElementTree       │                       │
     │               │◄─────────────────────┤                       │
     │               │ JsonSerializer       │                       │
     │ JSON string   │ .Serialize(tree)     │                       │
     │◄──────────────┤                      │                       │
```

### Data Transformations

```
1. MCP Request
   └─► no parameters (returns foreground window state)

2. UIAutomationService.GetStateAsync()   [on the STA worker thread]
   ├─► Foreground root: GetForegroundWindow() → FromHandle, falling back to
   │   the focused element, then the desktop (UiRoots.FirstAvailable: a rung
   │   that throws — FocusedElement() with nothing in front — moves on)
   ├─► BuildTree(root, depth: 3, budget) — recursive, depth-limited
   └─► ElementBudget.TryTake() per node (UiTreeOptions.MaxElements,
       from --max-tree-elements, default 500); a refused child ends its
       parent's child list and stops the walk

3. Per element → ElementInfo:
   { ElementId "el_N" (in the element cache's bounded LRU — 5 000 by default,
     least recently used out first — for get_element/interact_element), Name,
     ControlType, IsEnabled, IsOffscreen, Bounds, Value, IsChecked,
     IsSelected, Scroll (null here — snapshot populates it) }

4. ElementTree aggregate:
   ElementTree {
     Root: ElementInfo, Children: ElementTree[],
     Truncated / ElementLimit  ← set on the ROOT only when the budget
                                  stopped the walk; omitted from the JSON
                                  otherwise
   }

5. MCP Response: JSON string of ElementTree
```

---

## Snapshot Data Flow (whole desktop)

`snapshot` is the entry point for an interaction loop: one call returns the window list, the
cursor, every interactive element with its centre coordinates, and the scrollable regions.

### Data Transformations

```
1. MCP Request
   └─► snapshot(scope, window?, include_tree, max_elements, format, use_dom)
       UIAutomationTools validates in order: scope → the window/scope rule →
       max_elements ≥ 0 → format; use_dom is passed straight through

2. UIAutomationService.SnapshotAsync(SnapshotRequest)
   ├─► Header, each collaborator read once:
   │     IInputService.GetCursorPositionAsync()      → Cursor
   │     IWindowService.EnumerateMonitorsAsync()     → CursorMath.MonitorIndexOf
   │     IWindowService.ListAsync(...)               → Windows, WindowFilter.ActiveOf
   ├─► Roots by scope: desktop = every non-minimised window (topmost first),
   │   foreground = the active entry (else UIA's foreground window),
   │   window = title match, exact then substring
   ├─► use_dom: a target window with IsBrowser is re-rooted on its page —
   │   FindPageDocument (Document + AutomationId "RootWebArea", retried
   │   because Chromium fills its tree lazily); no page → the whole window
   │   is walked and the Pages entry carries DomCorrection.NoPageNote
   └─► [STA thread] one ElementBudget for the whole call;
       UiTraverser.Walk(root, title, budget) per window under one CacheRequest;
       a window whose walk throws is logged and skipped

3. Per walked node → el_N id (ids the previous snapshot issued are evicted
   first) → UiTree.Project:
   ├─► UiClassifier.Classify → Interactive?  → SnapshotElement
   │     (Window, ControlType, Name, CenterX/CenterY from CenterOf(Bounds),
   │      Action from ActionFor, Focused, IsPassword, Value (null when
   │      password), Toggle, Expand, Shortcut, Range min/value/max)
   ├─► UiClassifier.IsScrollable → SnapshotScrollable (+ ScrollInfo)
   └─► DOM page: DomCorrection.SuppressesInteractive drops the page document
       from the interactive list (it keeps its id and its scrollable row),
       DomCorrection.PageFor projects the walk onto SnapshotPage
       (DocumentId, Title, Url, Scroll, Text in document order)

4. SnapshotResult { Windows, ActiveWindow, Cursor, CursorMonitorIndex,
                    Interactive[], Scrollable[], Tree?, Truncated,
                    ElementLimit, ElementCount, CaptureMs,
                    Stages? (--profile-snapshot: header, walk),
                    Pages?  (use_dom only; null otherwise, so a plain
                             snapshot serialises exactly as before) }

5. MCP Response:
   format="text" (default) → SnapshotRenderer.Render(result)  — compact rows,
                             then the Pages section, the truncation note and
                             the "Timing:" line when each applies
   format="json"           → JsonSerializer.Serialize(result)
```

---

## Click Data Flow (and the shared target resolver)

`click`, `type`, `scroll` and `drag` all start at `InputTools.ResolveTargetAsync` (roadmap C1),
so the point is decided — and an unreachable element refused — before any input is injected. B-7's
`multi_select` and `multi_edit` run that same resolver over *every* entry of their JSON array
(parsed by the pure `BatchTargets`) before the first click, so a batch holding one off-screen
element is refused with nothing done; the entries then run in order and stop at the first failure,
reporting `failedIndex`/`error` with the results so far. `multi_select` wraps the run in
`KeyDownAsync("ctrl")` … `KeyUpAsync("ctrl")` with the release in a `finally`; `multi_edit` types
each entry through B-1's `TypeAsync(text, TypeOptions(clear, Idle, press_enter, 5))`.

```
┌──────────┐   ┌────────────┐   ┌──────────────────┐   ┌────────────────────┐
│ AI Agent │   │InputTools  │   │  InputService    │   │ Win32 (user32) +   │
│          │   │            │   │  UIAutomationSvc │   │ H.InputSimulator   │
└────┬─────┘   └─────┬──────┘   └────────┬─────────┘   └─────────┬──────────┘
     │               │                   │                        │
     │ Click(x?,y?,  │                   │                        │
     │  element_id?, │                   │                        │
     │  button,      │                   │                        │
     │  clicks)      │                   │                        │
     ├──────────────►│                   │                        │
     │               │ ParseButton(btn)  │                        │
     │               │ ResolveTarget:    │                        │
     │               │  GetElementAsync  │  (element_id only —    │
     │               ├──────────────────►│   a coordinate call    │
     │               │  ElementInfo      │   never touches UIA)   │
     │               │◄──────────────────┤                        │
     │               │ ElementTarget     │                        │
     │               │  .CentreOf(info)  │  (off-screen / no      │
     │               │  → (x, y)         │   bounds → refused)    │
     │               ├──────────────────►│                        │
     │               │  ClickAsync(x,y,  │                        │
     │               │  MouseButton,     │  (clicks:0 → HoverAsync│
     │               │  clicks)          │   and nothing pressed) │
     │               ├──────────────────►│                        │
     │               │                   │ SetCursorPos(x, y)     │
     │               │                   ├───────────────────────►│
     │               │                   │ GetCursorPos read-back │ (throws if Windows
     │               │                   │◄───────────────────────┤  clamped the point)
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
Input:  element_id="el_12", button="right", clicks=1

Processing:
  ParseButton("right") → MouseButton.Right
  ResolveTargetAsync(null, null, "el_12", allowCursor: false):
    ├─► IUIAutomationService.GetElementAsync("el_12")  // the element cache; answers under "el_12" itself
    └─► ElementTarget.CentreOf(info) → (800, 400)      // Bounds.X + Width/2, Bounds.Y + Height/2
  InputService.ClickAsync(800, 400, MouseButton.Right, 1):
    ├─► SetCursorPos(800, 400) + GetCursorPos   // physical virtual-desktop pixels; throws if clamped
    └─► IMouseSimulator.RightButtonClick()      // SendInput(MOUSE_RIGHT_DOWN + MOUSE_RIGHT_UP) at the cursor

Output: {"action":"click","x":800,"y":400,"button":"right","clicks":1,
         "elementId":"el_12","name":"Save","modifiers":null}
```

With `x`/`y` instead of the id the resolver returns the point unchanged and `elementId`/`name`
are `null`; `scroll` and `drag`'s origin allow no target at all, which reads
`IInputService.GetCursorPositionAsync()` and reports `cursor`.

With `modifiers` (E-3, e.g. `"shift"` or `"ctrl+shift"`), `ParseModifiers` runs before
`ResolveTargetAsync` — an unknown name, `win`, or a modifier with `clicks:0` is refused with no
input sent — and the press becomes:

```
  HoverAsync(x, y, 0)                         // pointer first: an off-monitor point throws before any key
  HoldingAsync([ctrl, shift, alt] ∩ asked):
    ├─► KeyDownAsync("ctrl") … KeyDownAsync("alt")   // fixed order, whatever order the caller wrote
    ├─► ClickAsync(x, y, button, clicks)
    └─► KeyUpAsync in reverse, CancellationToken.None // also after a failed click or a cancellation,
                                                      // and for a key whose key-down threw; one retry
Output: {…, "modifiers":"ctrl+shift"}
```

A release that fails twice after the click was sent is an `InvalidOperationException` saying the
click was sent and naming the keys that may still be down; a failure of the click itself is
rethrown unchanged.

---

## Type Data Flow (keys or paste)

`type` resolves its target the same way, clicks it when one was given, and then hands the text to
the pure `TypePlanner` (roadmap C8); `InputService` executes the plan against its `IKeyboardSink`.

```
Input:  text="report.txt", element_id="el_7", clear=true, press_enter=true, pace_ms=5

Processing:
  ResolveTargetAsync(…, "el_7") → (420, 260)   → ClickAsync(420, 260, Left, 1)
  TypePlanner.Plan("report.txt", TypeOptions(Clear:true, Idle, PressEnter:true, 5)):
    ├─► shortcut "ctrl+a"      (Clear)
    ├─► key      "backspace"   (Clear)
    ├─► text     "report.txt"  (< 200 chars → keys; a "\n" here would be key "enter")
    └─► key      "enter"       (PressEnter)
  InputService.TypeAsync: each step on the sink, Task.Delay(PaceMs) between steps

  A ≥ 200-character step with no control characters other than \n/\t becomes ONE paste step:
    GetTextAsync() → previous  ·  SetTextAsync(text)  ·  shortcut "ctrl+v"
    · 150 ms settle · SetTextAsync(previous)          → clipboardRestored: true

Output: {"typed":10,"method":"keys","clipboardRestored":null,
         "x":420,"y":260,"elementId":"el_7","name":"File name"}
```

---

## Bring-to-Front Data Flow (window matching + the foreground ladder)

`switch_to_window`, `focus` and the seven acting `window` actions (`minimize`, `maximize`,
`restore`, `close`, and B-9's `move`, `resize`, `set_bounds`) share one target resolver and,
for the two focus tools, one foreground ladder. Windows refuses `SetForegroundWindow` to a
background process, so the outcome is re-read rather than assumed (roadmap C11).

### Data Transformations

```
1. MCP Request
   └─► switch_to_window(title?, hwnd?) | focus(title?, hwnd?)
       WindowTools refuses "neither given" with an ArgumentException before
       the inventory is read; window(action, title?, hwnd?) validates the
       action first, then that one of title/hwnd is present — except
       move/resize/set_bounds, which check their own x/y/width/height rule
       and fall back to the foreground window when neither is given

2. WindowService.BringToFrontAsync(title, hwnd)  |  ExecuteAsync(action, title,
   hwnd)  |  SetBoundsAsync(title, hwnd, x, y, width, height, restoreFirst)
   └─► ListAsync(includeMinimized: true, includeHidden: false)   — A-1's inventory
       (SetBoundsAsync with neither title nor hwnd skips it and takes
        GetActiveAsync instead: Strategy="foreground", Score=100)

3. WindowMatcher.Match(inventory, title, hwnd)   — pure
   ├─► hwnd given      → that window, Strategy="hwnd", Score=100 (never fuzzes)
   ├─► exact title     → Strategy="exact",     Score=100   (OrdinalIgnoreCase)
   ├─► substring       → Strategy="substring", Score=100
   ├─► fuzzy           → max(FuzzyMatch.PartialRatio, TokenSetRatio) ≥ 70
   └─► nothing matched → KeyNotFoundException listing up to 15 open titles
       Ties inside one strategy go to the lowest ZOrder (the frontmost);
       minimised windows are candidates

4a. window(minimize|maximize|restore|close): ShowWindow / PostMessage(WM_CLOSE)
    on the matched handle
    └─► WindowAction { Action, Title (the matched window's), Success=true,
                       MatchStrategy, Score, Hwnd }

4b. window(move|resize|set_bounds): WindowGeometry.Apply(match, x?, y?, width?,
    height?, restore_first, IWindowGeometryNative)   — pure decisions,
    Win32WindowGeometryNative in production
    ├─► IsIconic / IsZoomed → refuse naming the state, unless restore_first,
    │   which sends ShowWindow(SW_RESTORE) first          → Restored=true
    ├─► GetWindowRect                                     → Before
    ├─► SetWindowPos(hwnd, null, x ?? Before.X, y ?? Before.Y,
    │       width ?? Before.Width, height ?? Before.Height,
    │       SWP_NOZORDER|SWP_NOACTIVATE
    │       [|SWP_NOMOVE when no x/y] [|SWP_NOSIZE when no width/height])
    ├─► GetWindowRect again                               → After (the outcome)
    └─► WindowBoundsResult { Window, Before, After, MatchStrategy, Score,
                             Restored }

4c. ForegroundLadder.Bring(match, IForegroundNative)  — Win32ForegroundNative in
    production, a recording fake in the tests
    ├─► IsIconic → ShowWindow(SW_RESTORE)                    → Restored=true
    ├─► rung 1: SetForegroundWindow                        → "SetForegroundWindow"
    ├─► rung 2: AttachThreadInput + BringWindowToTop +
    │           SetForegroundWindow + detach              → "AttachThreadInput"
    │           (skipped whole when the attach is refused — elevated target)
    ├─► rung 3: keybd_event(VK_MENU) down/up +
    │           SetForegroundWindow                       → "AltNudge"
    └─► GetForegroundWindow is re-read after every rung and is the only source
        of Success/Strategy; SetForegroundWindow's return value is never consulted

5. MCP Response: ForegroundResult { Window, MatchStrategy, Score, Restored,
                                    Strategy (null when no rung worked), Success }
```

---

## Launch Data Flow (path → catalog → activate → wait)

`launch` resolves a name the way a person says it, starts the app, and then reports the window it
actually produced rather than the request it sent (roadmap C7 and C11). Nothing here spawns
PowerShell.

### Data Transformations

```
1. MCP Request
   └─► launch(app_name, wait_for_window = true, timeout_ms = 10000)
       WindowTools rejects a blank app_name and a timeout_ms outside 1..60000
       before the service is touched

2. WindowService.LaunchAsync(appName, waitForWindow, timeoutMs)
   └─► with waitForWindow: ListAsync(true, false) FIRST — the set of Hwnds that
       were already open, so a title match afterwards can only be a NEW window

3a. IsPathOrExecutable(appName)      — File/Directory.Exists, or a name ending
    in ".exe" found on PATH; a bare word like "calc" is NOT a path
    └─► IAppActivator.StartShortcutOrPath(appName)  → pid
        Kind="path", Strategy="path", Score=100, MatchedName=appName

3b. otherwise IAppCatalogService.ResolveAsync(appName)
    ├─► ListAsync(): the two Start Menu Programs folders' *.lnk files +
    │   PackageManager.FindPackagesForUser("") → GetAppListEntriesAsync()
    │   (display name + AUMID), merged by AppCatalog.Merge, cached 5 min
    ├─► AppCatalog.Match: exact → prefix (shortest name) → fuzzy
    │   max(PartialRatio, TokenSetRatio, 95 − unread words when the request
    │   abbreviates the name) ≥ 70 (highest, ties shortest name)
    ├─► a top tie with an abbreviation in it → ArgumentException naming the
    │   tied names ("'wt' could mean more than one app: …"); no refresh,
    │   nothing launched
    ├─► a miss refreshes the cache ONCE, then re-matches
    └─► still nothing → KeyNotFoundException naming the five nearest with scores
    └─► IAppActivator.ActivatePackaged(AUMID)      → pid   (packaged)
        IAppActivator.StartShortcutOrPath(.lnk)    → pid   (shortcut)

4. wait_for_window:false → LaunchResult with Hwnd/Title null, WindowDetected=false
   wait_for_window:true  → LaunchWait.ForWindowAsync(ListAsync, pid, matchedName,
                             before, timeout_ms, pollMs = 250)
   └─► LaunchWait.Pick per poll: a window with that Pid (lowest ZOrder first),
       else a window NOT in `before` whose title matches matchedName
       exact → substring → fuzzy ≥ 70  (packaged apps and browsers hand off to
       a process the activation never named)
   └─► timeout → null, which is WindowDetected:false — an outcome, not an error

5. MCP Response: LaunchResult { MatchedName, Kind (shortcut|packaged|path), Score,
                                Pid, Hwnd?, Title?, WindowDetected,
                                Strategy (path|exact|prefix|fuzzy) }
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
     │              │ RunAsync(command,  │                          │
     │              │   timeout)         │                          │
     │              ├───────────────────►│                          │
     │              │                    │ acquire serialization    │
     │              │                    │ gate, then start the     │
     │              │                    │ clock: the earlier of    │
     │              │                    │ timeout and the 15-min   │
     │              │                    │ execution backstop       │
     │              │                    │                          │
     │              │                    │ PowerShellInvocation:    │
     │              │                    │  -EncodedCommand (or     │
     │              │                    │  temp .ps1 -File if big) │
     │              │                    │                          │
     │              │                    │ Process.Start()          │
     │              │                    │  powershell.exe          │
     │              │                    │  -NoProfile              │
     │              │                    │  -NonInteractive         │
     │              │                    ├─────────────────────────►│
     │              │                    │ close stdin; read        │
     │              │  progress          │ stdout+stderr; on cancel │
     │  progress    │  heartbeat / 10s   │ or a clock: kill the     │
     │  notification│  while waiting     │ tree, harvest the pumps  │
     │◄─────────────┤                    │◄─────────────────────────┤
     │              │ PSResult           │                          │
     │              │◄───────────────────┤                          │
     │ JSON string  │                    │                          │
     │◄─────────────┤                    │                          │
```

`timeout_seconds` (C-6, 1–900) is what the tool turns into that `timeout`; `0` passes `null` and
leaves the backstop as the only clock. A clock expiry is **not** an exception on this path: the
tree is killed, `HarvestAsync` gives the two pumps a 2-second grace to drain what the script wrote
before it died, and the tool serialises a `PSResult` with `TimedOut:true`, `Success:false`,
`ExitCode:-1` and `"timed out after Ns"` last in `Errors`. The one-argument
`RunAsync(command, ct)` every *internal* caller uses turns the same expiry into a caller-facing
`TimeoutException` instead, so no service parses a partial stdout as an answer.

`background:true` skips this path entirely: ShellTools calls `JobService.StartAsync`, which
builds the child via the same `PowerShellInvocation` helper but runs it **outside** the
serialization gate, pumps stdout/stderr into bounded buffers, and returns a `JobInfo`
immediately. The agent then polls via the `job` tool (`status`/`output`/`cancel`/`list`);
a per-job 60-min backstop tears down runaway jobs as `timedOut`.

### Data

```
Input:  command = "Get-Process | Select-Object Name,CPU | ConvertTo-Json"

Processing:
  1. Acquire the serialization gate (one PowerShell at a time), then arm the clock —
     the caller's timeout when it is the shorter, else the 15-min backstop
  2. PowerShellInvocation.BuildArgumentsAsync — UTF-8 preamble + -EncodedCommand
     (temp .ps1 -File fallback for oversized scripts), under the CALLER's token only
  3. Process.Start(powershell.exe); close stdin (protects the MCP stdio channel)
  4. Await exit (ShellTools reports a progress heartbeat every 10s meanwhile);
     both streams are pumped into 1 000 000-char bounded buffers, tail kept

Output: PSResult { Success=true, Stdout="[{...}]", Stderr="", ExitCode=0, Errors=[],
                   TimedOut=false, StdoutTrimmedChars=0, StderrTrimmedChars=0 }
        → JSON: {"Success":true,"Stdout":"[{...}]","Stderr":"","ExitCode":0,"Errors":[],
                 "TimedOut":false,"StdoutTrimmedChars":0,"StderrTrimmedChars":0}
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

Plain list (C-3): ListAsync(ProcessListOptions) does not use WMI at all — Process.GetProcesses(),
one TotalProcessorTime reading timestamped per process, a 250 ms wait, a second reading, then
CpuSample.Percent per process (normalised across all cores) and CpuSample.SortAndLimit for
sort_by/limit. The lineage, group and orphan shapes above carry no CPU column, which is why
sort_by and limit are refused rather than ignored with them. ListAsync(nameFilter) — the overload
the name kill and startup_report use — keeps its old shape and does not sample.

Graceful kill (C-3): KillAsync(pid, KillOptions) runs the start-time guard first when one is
given, then posts WM_CLOSE through IProcessWindowNative to every visible top-level window the pid
owns, waits for the exit bounded by GraceMs, and calls Kill() only if it is still alive. Nothing
to post to = forced at once, WaitedMs 0. Output: {killed:[{pid, name, graceful, exitedGracefully,
forced, waitedMs}]} for a pid or name kill; the tree kill keeps its text count.
```

---

## Screenshot + OCR Data Flow

```
┌──────────┐   ┌───────────┐   ┌──────────────────┐   ┌─────────────────────┐
│ AI Agent │   │ScreenTools│   │ScreenshotService │   │ SkiaSharp / GDI+/WGC│
│          │   │           │   │   OcrService     │   │ Windows.Media.Ocr   │
└────┬─────┘   └─────┬─────┘   └────────┬─────────┘   └──────────┬──────────┘
     │               │                  │                         │
     │ Screenshot()  │                  │                         │
     ├──────────────►│ resolve rect:    │                         │
     │               │ monitors, region │                         │
     │               │ or display; then │                         │
     │               │ read the cursor  │                         │
     │               │ annotate: one    │                         │
     │               │ SnapshotAsync    │                         │
     │               │ (desktop), keep  │                         │
     │               │ what overlaps r  │                         │
     │               │ CaptureAsync(r,  │                         │
     │               │  CaptureOptions) │                         │
     │               ├─────────────────►│                         │
     │               │                  │ AcquireFrame: gdi | wgc │
     │               │                  ├────────────────────────►│
     │               │                  │◄────────────────────────┤
     │               │                  │ cursor: icon or ring    │
     │               │                  │ ScaleMath.Fit → resize  │
     │               │                  │ Annotator.Draw on a copy│
     │               │                  │ SKBitmap.Encode(jpg/png)│
     │               │                  ├────────────────────────►│
     │               │ ScreenshotResult │◄────────────────────────┤
     │               │ flash: glow on r │                         │
     │ metadata text │◄─────────────────┤                         │
     │ + element list│                  │                         │
     │ + image block │                  │                         │
     │◄──────────────┤                  │                         │
     │               │                  │                         │
     │ Ocr(region)   │                  │                         │
     ├──────────────►│ same resolver,   │                         │
     │               │ ExtractTextAsync │                         │
     │               ├─────────────────►│                         │
     │               │                  │ OcrEngine.RecognizeAsync│
     │               │                  ├────────────────────────►│
     │               │                  │  (Windows.Media.Ocr)    │
     │               │ recognised text  │◄────────────────────────┤
     │◄──────────────┤◄─────────────────┤                         │
```

**Annotate path (A-6).** The snapshot walk runs **after** the rect is resolved and the cursor read
and **before** the capture, so label N in the picture is row N of the text block from the same
call. Only elements and scrollables whose bounds overlap the captured rect are kept (half-open —
one touching the far edge is out), in snapshot order; the kept elements become
`AnnotationBox(ElementId, Bounds)` in `CaptureOptions.Annotations`. `ScreenshotService` draws them
after the downscale on a copy of the bitmap and returns `AnnotationsDrawn` (boxes that landed, not
boxes requested). The tool then emits metadata (`annotated`, `annotations`, and `grid` when a grid
was asked for), `SnapshotRenderer.Render` of the filtered snapshot, and the image — three blocks
inline, two with `output="file"`. A grid alone (`grid_columns`/`grid_rows` without `annotate`)
needs no walk. Because the walk is a snapshot walk, it evicts the previous `snapshot`'s `el_N` ids.

**Backend selection (A-10).** The call's `backend` argument is validated with the other arguments,
before anything is captured. `ScreenshotService.ResolveBackend` turns `auto` into the process
default (`--screenshot-backend` / `WINDOWSMCP_SCREENSHOT_BACKEND`, itself `auto` unless set), then
`AcquireFrame` asks the compositor (`WgcCaptureBackend`: one `GraphicsCaptureItem` per monitor the
rect touches → D3D11 staging texture → blit into the rect) or copies the screen with GDI. `auto`
falls back to GDI silently when WGC is unsupported or refuses; an explicit `wgc` throws instead.
Whatever produced the frame is what `ScreenshotResult.Backend` and the metadata `backend` field
report — never `auto`. The cursor overlay, downscale, annotations and grid run identically on
either frame.

**Flash and profiling (A-14).** `IFlashOverlay.Hide()` runs immediately before every capture —
unconditionally, so a glow left over from the previous call can never appear in the picture — and
`Show(r, 3.5 s)` runs immediately after when `--flash` is on. The tool then reports `flash: true`
from `IFlashOverlay.IsVisible`, i.e. what happened, not what was asked (a host with no window
station shows nothing). With `--profile-snapshot` the tool's own stages (`resolve`, `cursor`,
`snapshot` when annotating, `capture`) are merged with the service's finer-grained ones
(`capture`, `cursor`, `resize`, `encode` — the service wins a name clash) into the metadata
`stages` object, and the same numbers go to stderr at Information.

**Control indicator (S-11).** `CaptureAsync` acquires the frame — including an `auto` → GDI
fallback — inside one `IControlIndicator.SuspendForCapture()` scope, so the ring and banner are
never in a `screenshot`, an annotated one or an `ocr` read. On Windows 10 2004+ the windows carry
`WDA_EXCLUDEFROMCAPTURE` and the scope is a no-op; below that build it takes them down before the
grab and lets them back when it is disposed. A capture never starts the indicator's thread.

---

## Control Indicator Flow (S-11)

How a driving tool shows the ring and banner, and how a takeover reaches the agent. Nothing in
this flow refuses, delays or cancels a call, and no physical input is blocked:

```
call-tool filter (WindowsMcpHost.AddWindowsMcp)
        │
  name in DrivingTools.Names?  (click, drag, hover, type, key, shortcut, scroll, multi_select,
        │                       multi_edit, interact_element, file_dialog, focus, switch_to_window)
  ┌─────┴────────────────────────────┐
  │ yes                              │ no
  ▼                                  │
BeginAction()  ── refcount++, clear  │
  │  any dismissal; one tick on the  │
  │  overlay thread, waited ≤ 50 ms: │
  │  RegisterHotKey + paint + show   │
  ▼                                  ▼
await next(ctx)  — the tool runs unchanged (a caller-facing exception becomes an error result)
  │
  ▼  (finally)
EndAction()  ── refcount--; the last one starts the 5 s fade (monotonic clock)
  │
  ▼
call cancelled?  ── yes → leave any takeover pending; return the result
  │ no
  ▼
ConsumeTakeover() → TakeoverRecord?  ── null → return the result unchanged
  │ a record
  ▼
result.Content += TextContentBlock("Note: the user pressed <Hotkey> at HH:mm:ss (N s ago) and
                  took over the mouse and keyboard; the screen may have changed since your last
                  snapshot.")          ← any tool, an IsError result included; delivered once

overlay thread (16 ms tick while shown, 100 ms otherwise):
  ShouldShow? ── yes, not shown → Show (register the hotkey, paint at the pointer, show)
              ── no, shown      → Hide (unregister the hotkey, hide)
  shown → follow the pointer (ring), the pointer's monitor (banner)
  WM_HOTKEY while shown → dismiss at once, record {At = now (UTC), Hotkey}; the next BeginAction
                          shows it again
```

Every indicator call in the filter is wrapped so that a fault in it is swallowed: the indicator
can never fail or skip a tool call. A tool that throws a non-caller-facing exception leaves the
filter before `ConsumeTakeover` (the SDK masks it), so that takeover too waits for the next
result. A response lost **after** the filter returns still spends the note — the cancellation
check narrows that window but cannot close it. With `--control-indicator off`, every member is a
no-op and nothing is created or registered.

---

## WaitFor Data Flow (Polling Loop)

`WaitFor` is the only tool with internal retry logic — all other tools are single-pass. Since B-6
it waits for one of five conditions, and each poll gathers **only** the evidence its condition
reads:

```
UIAutomationTools.WaitFor(text, timeout_ms, interval_ms, kind, scope, window,
                          include_offscreen, condition, use_dom)
        │  parse condition (element_exists|element_enabled|focused_element|text_exists|
        │   active_window; aliases element|enabled|focused|text|window), validate
        │   timeout_ms 0..120000, interval_ms 0..5000, non-blank text, ParseTarget(scope, window)
        ▼
UIAutomationService.WaitForAsync(WaitRequest)  →  WaitLoopAsync(request, gather, ct)
        │
        │   gather = the evidence that condition reads, and nothing else:
        │     active_window                → IWindowService.ListAsync(...)      (NO UI walk)
        │     text_exists|focused_element  → SnapshotAsync(SnapshotRequestFor(request))
        │                                    (scope mapped, no tree, server budget, UseDom)
        │     element_exists|_enabled      → FindElementAsync(text, kind, scope, …)
        ▼
  ┌──────────────────────────────────────────────────────────────┐
  │ clock = Stopwatch   attempts = 0   anyCleanPoll = false       │
  │ lastDetail = ""     lastFailure = null                        │
  │                                                               │
  │  ┌──────────────────────────────────────────┐                 │
  │  │ attempts++ ; evidence = await gather(ct) │◄────────────┐   │
  │  └──────────────┬───────────────────────────┘             │   │
  │                 │                                          │   │
  │        threw? ──┤ YES → lastFailure = ex ─────┐            │   │
  │           NO    │                             │            │   │
  │                 ▼                             │            │   │
  │        WaitConditions.Evaluate(condition,     │            │   │
  │          text, evidence) → (satisfied,        │            │   │
  │          detail, element?)                    │            │   │
  │        anyCleanPoll = true ; lastDetail=detail│            │   │
  │        satisfied? ┬─ YES → WaitForResult(true,│            │   │
  │                   │   name, elapsed, attempts,│            │   │
  │                   │   detail, element)        │            │   │
  │                   NO ◄────────────────────────┘            │   │
  │                   ▼                                        │   │
  │        remaining = timeout_ms − clock.Elapsed              │   │
  │           ≤ 0 ? ─┬── NO → await Task.Delay(               │   │
  │                  │        min(max(10, interval_ms),        │   │
  │                  │            remaining)) ─────────────────┘   │
  │                 YES                                            │
  │                  ▼                                             │
  │        WaitForResult(false, name, elapsed, attempts,           │
  │          anyCleanPoll ? lastDetail                             │
  │                       : "every poll failed: <last message>")   │
  └──────────────────────────────────────────────────────────────┘
```

A poll that throws is **retried**, never fatal — absorbing transient UIA failure is the whole
point of a wait (checklist D-5). The loop polls at least once, so `timeout_ms: 0` means "check
now". Roadmap C4: the call **always** answers with a `WaitForResult` — a timeout is
`Satisfied:false` carrying the last poll's `Detail`, and the case the old loop threw on (every
poll failed) is that same result with a detail saying so, not a `TimeoutException`. The tool
serialises the result, so the `"null"` string it used to return on a miss is gone.

The original `WaitForAsync(text, …)` overload and its `PollAsync` loop — `null` on a clean miss,
`TimeoutException` when every poll failed — are still on `IUIAutomationService`, but no tool
calls them any more.

---

## DI Resolution Flow at Startup

```
Host.CreateApplicationBuilder(args)
        │
        ▼
builder.Services.AddSingleton<IInputService, InputService>()
  ...  (40 services + the ScreenshotOptions record from --screenshot-scale,
       --flash, --profile-snapshot and --screenshot-backend, the
       UiTreeOptions record from --max-tree-elements and --profile-snapshot,
       the TransportOptions record from --transport: Stateless over HTTP,
       which is how WebTools knows sampling cannot reach the client there,
       and the ControlIndicatorOptions record from --control-indicator and
       --control-hotkey)
        │
        ▼
builder.AddWindowsMcp(options)    ← Hosting/WindowsMcpHost: AddMcpServer(...) + filter + WithToolsFromAssembly()
    .WithStdioServerTransport()   ← or .WithHttpTransport(stateless) + MapMcp("/mcp") with --transport http
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
  │            ← IUIAutomationService (element_id) │
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
UiTraverser.ReadNode — is the element something the model can see?
        │
        ▼
  ┌─────────────────────────┐
  │ IsOffscreen == false     │──NO──► Skip (and its subtree)
  │ (an Edit with real       │
  │  bounds is kept — D-7)   │
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐   NO ─► Skip the node, still walk
  │ Bounds clipped to the   │        its children (zero-area
  │ window rect, area > 0   │        containers hold real ones)
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ ElementBudget.TryTake() │──NO──► Truncated = true, walk stops
  └──────────┬──────────────┘
             │YES
             ▼
   [UiNode] ──► UiClassifier.Classify
        │
        ├─► ControlType in InteractiveControlTypes ──► Interactive
        ├─► LegacyIAccessible role in InteractiveLegacyRoles ──► Interactive
        │   ("text" only when the node carries a value)
        ├─► ControlType in InformativeControlTypes ──► Informative
        └─► otherwise ──► Structural
   (a node with a movable ScrollPattern is additionally listed as Scrollable)


Interactive control types (UiClassifier.InteractiveControlTypes, 17):
  Button, ListItem, MenuItem, Edit, CheckBox, RadioButton, ComboBox,
  Hyperlink, SplitButton, TabItem, TreeItem, DataItem, HeaderItem,
  Spinner, Slider, ScrollBar, Document

Informative control types (never clicked, worth reporting):
  Text, Image, StatusBar, ProgressBar, ToolTip, Header
```

---

## AssertElement Data Flow

```
AssertElement(element_id, state, expected?)
        │
        ▼
UIAutomationService.AssertElementAsync(element_id, state, expected)
        │
        ▼
  Validate: known state; `expected` only — and always — with "value"
        │
        ▼
  Resolve element by ID from the element cache (D-11)
  ├─ dead id + "exists" → FAIL "element id has expired (take a new snapshot)"
  │                        or FAIL "unknown element id" (never issued)
  └─ dead id + other    → KeyNotFoundException ("… has expired …" / "Unknown element id …")
        │
        ▼
  Liveness probe: ProcessId <= 0, or UIA_E_ELEMENTNOTAVAILABLE /
  RPC failure on any read (IsElementGone)
  └─ gone → FAIL "element no longer available"
        │
        ▼
  switch (state)
  ├─ "exists"  → PASS
  ├─ "enabled" → IsEnabled (UIA default true)              FAIL: "disabled"
  ├─ "checked" → TogglePattern state == On                 FAIL: "toggle state Off" / "no TogglePattern on …"
  ├─ "visible" → !IsOffscreen && bounds non-empty          FAIL: "offscreen" / "empty bounds"
  ├─ "focused" → HasKeyboardFocus, or == FocusedElement()  FAIL: "focus is on <type> '<name>'" / "nothing has focus"
  └─ "value"   → ValuePattern value (else Name) == expected, ordinal
                                                           FAIL: "value is '<actual>' (from ValuePattern|Name)"
        │
        ▼
  AssertResult(ElementId, State, Pass, Observed)
        │
        ▼
  Tool: "PASS" or "FAIL: {state} — observed {Observed}"
```

---

## Error Handling Flow

### PowerShell Execution Errors

```
PowerShellService.RunAsync(command, timeout)
        │
        ▼
  timeout <= 0  →  ArgumentException (there is no command blocklist)
        │ (passes)
        ▼
  gate → clock → Process.Start(...)
        │
  ┌─────┴───────────────┬──────────────────┐
  ▼                     ▼                  ▼
Success              Clock fired        Exception
  │                     │                  │
  ▼                     ▼                  ▼
PSResult{...}       Kill tree, harvest   Return PSResult{
                    the pumps, return      Success=false, Stdout="",
                    PSResult{              Stderr=ex.Message,
                      TimedOut=true,       ExitCode=-1,
                      Success=false,       Errors=[ex.Message]}
                      ExitCode=-1, …}
```

The one-argument `RunAsync(command, ct)` wraps the middle branch: it rethrows the timeout as a
caller-facing `TimeoutException` carrying the same reason, so an internal caller never parses a
partial stdout as data. The caller's own cancellation is an `OperationCanceledException` on every
overload.

### UI Automation Errors

```
find_element / snapshot / get_state walks
  └─► a property read or element that fails mid-walk is skipped; a window whose walk throws
      is logged and skipped

Element-targeted calls (get_text, get_table, interact_element; FocusAsync) — D-10 / D-11
  ResolveCached(id) misses  → KeyNotFoundException: "Element 'el_N' has expired: … take a new
                              snapshot" (issued, since dropped) | "Unknown element id …"
  acting verb (interact_element except scroll_into_view, FocusAsync) on a control that
  reports disabled          → InvalidOperationException before any pattern call or focus
  the target throws         → Refusing():
    timeout / cancellation / ArgumentException → rethrown as-is (no further read of a hung target)
    else liveness probe (one ProcessId read, UiRefusals.ProbeSaysGone)
      gone          → InvalidOperationException "Element 'el_N' is no longer available: …"
      alive         → UiRefusals.Translate: disabled | an item/part gone | not supported
                      → InvalidOperationException naming the control
      unrecognised  → rethrown unchanged (stays masked)
  
WaitFor — timeout path (B-6):
  elapsed > timeout_ms → WaitForResult(Satisfied=false, …, Detail=<the last poll's detail>)
  every poll threw     → the same result, Detail = "every poll failed: <last message>"
  Tool serialises it — a timeout is an outcome, never an exception and never "null"
```

### Which exception the caller sees (`ToolErrors`, the call-tool filter)

```
tool method throws
        │
        ▼
WindowsMcpHost.AddWindowsMcp → WithRequestFilters(AddCallToolFilter(...))
        │
  ToolErrors.IsCallerFacing(ex)?
        │
  ┌─────┴───────────────────────────────────────────────┐
  │ yes                                                 │ no
  ▼                                                     ▼
CallToolResult{IsError=true,                    the SDK's masking:
  Text = ToolErrors.MessageFor(ex)}             "An error occurred invoking '<tool>'."
  = ex.Message, cut at 2000 chars on a          (NullReference, IndexOutOfRange,
  whole character with " [cut: 2000-character    OutOfMemory, COMException,
  limit] …" appended                             Win32Exception, …)

Caller-facing: ArgumentException, InvalidOperationException, KeyNotFoundException,
IOException (FileNotFound / DirectoryNotFound / PathTooLong / "in use"),
UnauthorizedAccessException, TimeoutException.
The last four joined in C-1's round 4 — a refused parameter combination, the PID-reuse guard,
the window matcher's "Open windows: …", a stale element id, a missing registry key, an unknown
app, and every file tool's own not-found / access-denied answer with the path in it.
D-10: what a library or the OS throws for a refusal is translated at the call site into one of
these — UiRefusals (pattern, disabled, element gone), ProcessStartErrors (missing executable,
access denied, activation HRESULT), RegistryErrors (access denied naming hive and key),
PowerService (the OS refused the action), TaskSchedulerService.ParseTrigger (not a date-time).
ToolErrorsTests scans the server's IL so a new non-caller-facing construction fails the suite
unless it is on the reasoned, site-keyed MaskedOnPurpose list.
```

---

## Response Format

### Tool Response Shape

All tool methods return `Task<string>` — except `screenshot`, which returns
`Task<CallToolResult>` so it can carry an image block — where the string is either:
- **JSON** — from `JsonSerializer.Serialize(result)`
- **Plain string** — for simple acknowledgements (`"pressed ctrl+c"`, `"PASS"`)

`screenshot`'s `CallToolResult` is a `TextContentBlock` of metadata JSON followed by an
`ImageContentBlock`; with `output="file"` the image block is omitted and the metadata carries
the path. With `annotate:true` a second `TextContentBlock` — the rendered element list — sits
between the two, so an inline annotated capture carries three blocks and a file one carries two.

After a takeover (S-11), the next result of **any** tool — an error result included — carries
one more `TextContentBlock`, last, appended by the call-tool filter: `Note: the user pressed
<Hotkey> at HH:mm:ss (N s ago) and took over the mouse and keyboard; …`. It appears once.

### JSON Response Examples

```jsonc
// Click response
{"X":800,"Y":400,"Button":"Left","Clicks":1}

// PowerShell response
{"Stdout":"ProcessName  CPU\n---\npwsh   1.23\n","Stderr":"","ExitCode":0}

// GetState response (abbreviated) — an ElementTree; Truncated/ElementLimit
// appear on the root only when the element budget stopped the walk
{
  "Root": { "ElementId": "el_0", "Name": "Untitled - Notepad",
            "ControlType": "Window", "IsEnabled": true, "IsOffscreen": false,
            "Bounds": {"X":100,"Y":200,"Width":800,"Height":600},
            "Value": null, "IsChecked": null, "IsSelected": null, "Scroll": null },
  "Children": [
    { "Root": { "ElementId": "el_1", "Name": "OK", "ControlType": "Button", "...": null },
      "Children": [] }
  ]
}

// Snapshot response, format:"text" (default) — one element per row
Cursor: (1204, 733) on display 0
Active window: "Untitled - Notepad" (pid 8124, Normal)
Windows (z-order, topmost first):
  0. "Untitled - Notepad" [Normal] 1120x740 @ (280,150) pid=8124
Interactive (2 of 37, ids valid until the next snapshot):
window "Untitled - Notepad"
  el_3 (612,388) button "Save"  [action: click]  [shortcut: Ctrl+S]
  el_7 (840,470) document "Text editor"  [action: fill]  [focused]
Scrollable (1):
  el_7 (840,470) document "Text editor"  [v: 0%]  [h: 0%]  [reached top]
```

---

## Timing and Delays

| Location | Behavior | Notes |
|----------|----------|-------|
| `WaitFor` | Polls immediately, then every `interval_ms` (default 500 ms, range 0-5000, 10 ms floor) up to `timeout_ms` (default 10 s, range 0-120000); the sleep is clamped to the remaining budget | Only tool with a polling loop; a failed poll is retried, and a timeout — every-poll-failed included — is a `WaitForResult` with `Satisfied:false`, never a throw (roadmap C4) |
| `InputService` clicks | No delays: clicks are back-to-back `SendInput`; the cursor is placed with `SetCursorPos` and read back before any button event | A clamped (off-monitor) point throws rather than clicking elsewhere |
| `InputService.TypeAsync` | `TypeOptions.PaceMs` (default 5 ms) between the steps of a typing plan, never after the last one; a paste waits 150 ms after `ctrl+v` before restoring the previous clipboard text | The settle only runs on the real simulator sink, so the unit tests do not pay it. The target reads the clipboard on its own schedule, which is what the settle covers |
| `InputService.DragAsync(…, durationMs, steps)` | `durationMs / steps` between the interpolated moves (default 300 ms / 20 = 15 ms), including after the last one; `durationMs:0` moves without pausing | A drop target needs real motion, not a teleport; the button is released in a `finally`, so a cancelled drag never leaves it down |
| `wait` (`InputTools`) | The only tool whose *job* is a delay: one `Task.Delay(seconds)` on the request's cancellation token, `seconds` in (0, 60] | Validated in the tool before the delay; outside the range is an `ArgumentException` naming it and pointing at `wait_for`. No process is spawned — it replaces `powershell("Start-Sleep")`, which paid a cold start and took the serialization gate |
| `key` (`InputTools`, E-3) | `repeat:N` waits `RepeatPace` (5 ms) between presses, never after the last; `hold_ms:M` (0–10000) is one key-down, a `Delay` of M ms on the request's token, one key-up | Both delays go through the internal `InputTools.Delay` seam so the unit tests do not sleep. A cancelled hold still releases the key (on a non-cancellable token); a held key is not auto-repeated |
| `launch`'s window wait | `LaunchWait.ForWindowAsync` reads the inventory immediately, then every 250 ms until `timeout_ms` (default 10 000, max 60 000); the last sleep is clamped to the remaining budget | A timeout is `WindowDetected:false` with the pid, never an exception — a packaged app or a browser may hand its window to another process |
| `AppCatalogService` | The catalog is read from the Start Menu and the package manager at most once per 5 minutes; a resolve miss forces one extra refresh | Enumerating a few hundred packages costs ~1 s cold, which is why it is cached rather than read per `launch` |
| `ProcessService.ListAsync(ProcessListOptions)` | One 250 ms window (`CpuSampleWindow`) between the two `TotalProcessorTime` readings of every plain `process(list)` | Each reading is timestamped per process, so a walk of a few hundred processes does not over-credit the ones read first. `orphans`, `includeLineage`, `groupByRoot` and the `ListAsync(nameFilter)` overload do not sample and pay nothing; the delay is injected, so the unit tests do not pay it either |
| `ProcessService.KillAsync(pid, KillOptions)` | `graceful:true` waits up to `GraceMs` (default 3000, range 0–60000) after posting `WM_CLOSE` before forcing the process; the budget is **per process**, so a name kill matching five of them can wait five times over | A pid with no visible window is forced at once (`waitedMs:0`); a hard kill (`graceful:false`) waits nothing at all. When the wait times out, `HasExited` is read once more — a process that answered late is `exitedGracefully:true, forced:false`, not forced (round 4); a caller cancellation during the wait rethrows and kills nothing |
| `PowerShellService` | Async wait on process exit, bounded by the earlier of `timeout_seconds` (C-6, 1–900 s) and the 15-min execution backstop — both armed **after** the serialization gate, so a queued caller does not burn its budget waiting | A clock expiry kills the process tree and **returns** `TimedOut:true` with the partial stdout (the one-argument `RunAsync` throws a `TimeoutException` instead); after the kill the stream pumps get a 2 s `HarvestGrace` to drain before the result is built. Caller cancellation still kills the tree and throws |
| `scrape(summarize:true)` (`WebTools`) | The sampling request to the client's model is bounded by `SamplingTimeout` (120 s) on a token linked to the caller's | A client whose handler prompts a person who walks away would otherwise hang the call; the expiry returns the page text with a `Note`, never an error. Only the caller's own cancellation propagates |
| `ShellTools` heartbeat | Progress notification every 10s during a foreground `powershell` call | Lets spec-compliant clients reset their request timeout |
| `JobService` | Background jobs poll-based; per-job 60-min backstop | Runs outside the PowerShell serialization gate |
| `FlashOverlay` | The post-capture glow is up for 3.5 s, then taken down by its own timer — and unconditionally at the start of the next capture | `Show`/`Hide` marshal to the overlay thread and wait for it, bounded by a 2 s call timeout; both are silent no-ops with no interactive window station |
| `ControlIndicator` (S-11) | `BeginAction` waits at most 50 ms for the overlay thread to show the indicator; the thread ticks every 16 ms while it is shown (pointer follow + fade check) and every 100 ms otherwise; the indicator fades 5 s after the last driving call ends | The tool is never held up beyond that 50 ms and never refused. Below Windows 10 2004, `SuspendForCapture` waits up to 2 s for the windows to come down before a grab; with display affinity (2004+) it costs nothing |
| `NotificationService` | One retry, 1 s later, when the toast fails with `COMException 0x80070490` (element not found) — the platform has not picked a freshly written AppUserModelId registration up yet | The delay is injected, so the unit tests do not pay it; a second failure is `shown:false` with the reason in `note`, never a throw |
| `UIAutomationService.FindPageDocument` | `use_dom` only: up to 3 attempts for the `RootWebArea` document, 150 ms apart, no pause after the last | Chromium builds its accessibility tree lazily on the first query, so attempt 1 can miss a page that is there |
| MCP SDK transport (stdio or HTTP) | No artificial pauses — reads JSON-RPC frames continuously | Contrast: Python `pg.PAUSE = 1.0` |
