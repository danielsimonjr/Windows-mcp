# Windows-MCP Data Flow

## Overview

This document describes the data flow patterns within Windows-MCP, illustrating how information moves through the system from MCP tool invocation to Windows OS interaction and back.

---

## Primary Data Flow: State Capture

The `State-Tool` represents the most complex data flow in the system, orchestrating multiple subsystems to capture comprehensive desktop state.

### Sequence Diagram

```
┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌────────────────┐
│ AI Agent │   │ main.py  │   │ Desktop  │   │   Tree   │   │ Windows UI     │
│          │   │          │   │          │   │          │   │ Automation API │
└────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘   └───────┬────────┘
     │              │              │              │                 │
     │ State-Tool   │              │              │                 │
     │ (use_vision) │              │              │                 │
     ├─────────────►│              │              │                 │
     │              │ get_state()  │              │                 │
     │              ├─────────────►│              │                 │
     │              │              │ Tree(self)   │                 │
     │              │              ├─────────────►│                 │
     │              │              │              │ GetRootControl()│
     │              │              │              ├────────────────►│
     │              │              │              │◄────────────────┤
     │              │              │              │                 │
     │              │              │              │ get_appwise_    │
     │              │              │              │ nodes()         │
     │              │              │              ├─────────────────┤
     │              │              │              │                 │
     │              │              │              │ [Parallel]      │
     │              │              │              │ get_nodes()     │
     │              │              │              │ per app         │
     │              │              │              ├─────────────────┤
     │              │              │              │                 │
     │              │              │ TreeState    │                 │
     │              │              │◄─────────────┤                 │
     │              │              │              │                 │
     │              │              │ [if vision]  │                 │
     │              │              │ annotated_   │                 │
     │              │              │ screenshot() │                 │
     │              │              ├─────────────►│                 │
     │              │              │◄─────────────┤                 │
     │              │              │              │                 │
     │              │              │ get_apps()   │                 │
     │              │              ├──────────────┼────────────────►│
     │              │              │◄─────────────┼─────────────────┤
     │              │              │              │                 │
     │              │ DesktopState │              │                 │
     │              │◄─────────────┤              │                 │
     │              │              │              │                 │
     │ state_text   │              │              │                 │
     │ [+ Image]    │              │              │                 │
     │◄─────────────┤              │              │                 │
     │              │              │              │                 │
```

### Data Transformations

```
1. MCP Request
   └─► use_vision: bool

2. Desktop.get_state()
   ├─► Tree instance created
   ├─► TreeState obtained
   ├─► [Optional] Screenshot captured & annotated
   └─► Apps enumerated

3. Tree.get_state()
   ├─► Root control accessed
   └─► Parallel traversal initiated

4. Tree.get_appwise_nodes()
   ├─► Visible apps filtered
   └─► ThreadPoolExecutor spawns get_nodes() per app

5. Tree.get_nodes()
   ├─► DFS traversal of app UI tree
   ├─► Elements categorized (interactive/informative/scrollable)
   └─► DOM corrections applied

6. Data Aggregation
   TreeState
   ├── interactive_nodes: list[TreeElementNode]
   ├── informative_nodes: list[TextElementNode]
   └── scrollable_nodes: list[ScrollElementNode]

   DesktopState
   ├── apps: list[App]
   ├── active_app: App
   ├── screenshot: bytes (optional)
   └── tree_state: TreeState

7. MCP Response
   ├── Formatted text string
   └── [Optional] Image object
```

---

## Click Tool Data Flow

### Sequence

```
┌──────────┐   ┌──────────┐   ┌───────────┐   ┌──────────┐   ┌──────────┐
│ AI Agent │   │ main.py  │   │humancursor│   │ pyautogui│   │ Desktop  │
└────┬─────┘   └────┬─────┘   └─────┬─────┘   └────┬─────┘   └────┬─────┘
     │              │               │              │              │
     │ Click-Tool   │               │              │              │
     │ (loc,button) │               │              │              │
     ├─────────────►│               │              │              │
     │              │ move_to(loc)  │              │              │
     │              ├──────────────►│              │              │
     │              │               │ [cursor      │              │
     │              │               │  movement]   │              │
     │              │               ├─────────────►│              │
     │              │               │              │              │
     │              │ get_element_  │              │              │
     │              │ under_cursor()│              │              │
     │              ├──────────────────────────────┼─────────────►│
     │              │◄─────────────────────────────┼──────────────┤
     │              │               │              │              │
     │              │ mouseDown()   │              │              │
     │              ├──────────────────────────────►              │
     │              │ click()       │              │              │
     │              ├──────────────────────────────►              │
     │              │ mouseUp()     │              │              │
     │              ├──────────────────────────────►              │
     │              │               │              │              │
     │ result str   │               │              │              │
     │◄─────────────┤               │              │              │
```

### Data Flow

```
Input:
  loc: tuple[int, int]      # Coordinates
  button: 'left'|'right'|'middle'
  clicks: int               # Click count

Processing:
  1. cursor.move_to(loc)    # Human-like cursor movement
  2. desktop.get_element_under_cursor()  # Identify target
  3. pg.mouseDown() / pg.click() / pg.mouseUp()

Output:
  f'{num_clicks} {button} Clicked on {control.Name} at ({x},{y})'
```

---

## Type Tool Data Flow

```
Input:
  loc: tuple[int, int]
  text: str
  clear: bool

Processing:
  1. cursor.click_on(loc)           # Focus element
  2. desktop.get_element_under_cursor()
  3. [if clear] pg.hotkey('ctrl','a') + pg.press('backspace')
  4. pg.typewrite(text, interval=0.1)

Output:
  f'Typed {text} on {control.Name} at ({x},{y})'
```

---

## Launch Tool Data Flow

### Sequence

```
┌──────────┐   ┌──────────┐   ┌──────────┐   ┌───────────┐
│ AI Agent │   │ main.py  │   │ Desktop  │   │ PowerShell│
└────┬─────┘   └────┬─────┘   └────┬─────┘   └─────┬─────┘
     │              │              │               │
     │ Launch-Tool  │              │               │
     │ (name)       │              │               │
     ├─────────────►│              │               │
     │              │ launch_app() │               │
     │              ├─────────────►│               │
     │              │              │ Get-StartApps │
     │              │              ├──────────────►│
     │              │              │◄──────────────┤
     │              │              │               │
     │              │              │ [fuzzy match] │
     │              │              ├───────────────┤
     │              │              │               │
     │              │              │ Start-Process │
     │              │              ├──────────────►│
     │              │              │◄──────────────┤
     │              │              │               │
     │              │ (response,   │               │
     │              │  status)     │               │
     │              │◄─────────────┤               │
     │              │              │               │
     │ result str   │              │               │
     │◄─────────────┤              │               │
```

### Data Transformations

```
1. Input: name = "chrome"

2. PowerShell: Get-StartApps | ConvertTo-Csv
   Output: CSV with Name, AppID columns

3. Parse to dict: {'google chrome': 'Chrome.app', ...}

4. Fuzzy match: process.extractOne("chrome", keys)
   Result: ('google chrome', score)

5. Get AppID: apps_map['google chrome']

6. Execute: Start-Process "shell:AppsFolder\{AppID}"

7. Return: (response, status_code)
```

---

## UI Tree Traversal Data Flow

### Parallel Processing Flow

```
              GetRootControl()
                    │
                    ▼
          ┌─────────────────┐
          │  root.GetChildren()
          │  (all windows)   │
          └─────────────────┘
                    │
        ┌───────────┼───────────┐
        ▼           ▼           ▼
   ┌─────────┐ ┌─────────┐ ┌─────────┐
   │ Visible │ │ Visible │ │ Visible │
   │  App 1  │ │  App 2  │ │  App N  │
   └────┬────┘ └────┬────┘ └────┬────┘
        │           │           │
        ▼           ▼           ▼
   ThreadPoolExecutor.submit(get_nodes)
        │           │           │
        ▼           ▼           ▼
   ┌─────────┐ ┌─────────┐ ┌─────────┐
   │  DFS    │ │  DFS    │ │  DFS    │
   │Traversal│ │Traversal│ │Traversal│
   └────┬────┘ └────┬────┘ └────┬────┘
        │           │           │
        └───────────┼───────────┘
                    ▼
          ┌─────────────────┐
          │   as_completed  │
          │   (aggregate)   │
          └─────────────────┘
                    │
                    ▼
          ┌─────────────────┐
          │    TreeState    │
          │ interactive: [] │
          │ informative: [] │
          │ scrollable:  [] │
          └─────────────────┘
```

### DFS Traversal Logic

```
tree_traversal(node: Control):
    │
    ├─► is_element_interactive(node)?
    │   YES ─► Create TreeElementNode
    │          └─► dom_correction()
    │   NO ──┐
    │        │
    ├────────┴► is_element_text(node)?
    │           YES ─► Create TextElementNode
    │           NO ──┐
    │                │
    ├────────────────┴► is_element_scrollable(node)?
    │                   YES ─► Create ScrollElementNode
    │
    └─► for child in node.GetChildren():
            tree_traversal(child)
```

---

## Screenshot Annotation Data Flow

```
Input: nodes: list[TreeElementNode], scale: float

Processing:
  1. desktop.get_screenshot(scale)
     └─► pyautogui.screenshot()
     └─► thumbnail resize

  2. Add padding (20px border)
     └─► Image.new() + paste()

  3. For each node (parallel):
     ├─► Scale bounding box
     ├─► Generate random color
     ├─► Draw rectangle outline
     └─► Draw label (index number)

Output: Image.Image (annotated PNG)
```

### Coordinate Scaling

```
Original coordinates:
  box.left, box.top, box.right, box.bottom

Scaled + padded:
  left   = int(box.left * scale) + padding
  top    = int(box.top * scale) + padding
  right  = int(box.right * scale) + padding
  bottom = int(box.bottom * scale) + padding
```

---

## Element Visibility Determination

### Decision Flow

```
is_element_interactive(node)?
         │
         ▼
  ┌──────────────────┐
  │ControlTypeName   │
  │ in INTERACTIVE_  │──NO──►[Skip]
  │ CONTROL_TYPE_*   │
  └────────┬─────────┘
           │YES
           ▼
  ┌──────────────────┐
  │is_element_visible│──NO──►[Skip]
  └────────┬─────────┘
           │YES
           ▼
  ┌──────────────────┐
  │is_element_enabled│──NO──►[Skip]
  └────────┬─────────┘
           │YES
           ▼
  ┌──────────────────┐
  │NOT is_element_   │──NO──►[Skip]
  │    image         │
  └────────┬─────────┘
           │YES
           ▼
      [Interactive]


is_element_visible(node)?
         │
         ▼
  ┌──────────────────┐
  │IsControlElement  │──NO──►[Invisible]
  └────────┬─────────┘
           │YES
           ▼
  ┌──────────────────┐
  │BoundingRectangle │──YES─►[Invisible]
  │   .isempty()     │
  └────────┬─────────┘
           │NO
           ▼
  ┌──────────────────┐
  │ width * height   │──NO──►[Invisible]
  │    > 0           │
  └────────┬─────────┘
           │YES
           ▼
  ┌──────────────────┐
  │NOT IsOffscreen   │──NO──►[Invisible]
  └────────┬─────────┘
           │YES
           ▼
       [Visible]
```

---

## Response Formatting

### State-Tool Output Structure

```
Focused App:
{active_app.to_string()}

Opened Apps:
{apps[0].to_string()}
{apps[1].to_string()}
...

List of Interactive Elements:
Label: 0 App Name: {app} ControlType: {type} Control Name: {name} Shortcut: {key} Cordinates: ({x},{y})
Label: 1 ...
...

List of Informative Elements:
App Name: {app} Name: {text}
...

List of Scrollable Elements:
Label: {N} App Name: {app} Name: {name} Cordinates: ({x},{y}) Horizontal Scrollable: {bool} Vertical Scrollable: {bool}
...
```

### Vision Mode Additional Output

```python
return [state_text, Image(data=desktop_state.screenshot, format='png')]
```

---

## Error Handling Flow

### PowerShell Execution

```
execute_command(command)
         │
         ▼
  ┌──────────────────┐
  │subprocess.run()  │
  │ check=True       │
  └────────┬─────────┘
           │
     ┌─────┴─────┐
     ▼           ▼
 [Success]  [CalledProcessError]
     │           │
     ▼           ▼
  (stdout,    (e.stdout,
   0)          e.returncode)
```

### Tree Traversal Error Isolation

```
for future in as_completed(future_to_node):
    try:
        result = future.result()
        # process result
    except Exception as e:
        print(f"Error processing node: {e}")
        # Continue with other apps
```

---

## Timing and Delays

| Location | Delay | Purpose |
|----------|-------|---------|
| `Tree.get_state()` | 1.0s | Allow UI to settle before traversal |
| `Desktop.get_apps()` | 0.75s | Wait for window enumeration stability |
| `Tree.annotated_screenshot()` | 0.25s | Post-screenshot pause |
| `pg.PAUSE` | 1.0s | Between pyautogui operations |
| `pg.typewrite(..., interval=0.1)` | 0.1s | Between keystrokes |
