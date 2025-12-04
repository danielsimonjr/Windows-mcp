# Windows-MCP Component Reference

## Component Overview

This document provides detailed documentation for each component in the Windows-MCP architecture, including their responsibilities, interfaces, and implementation details.

---

## MCP Server Component (`main.py`)

### Purpose
Serves as the MCP protocol interface, exposing Windows automation capabilities as callable tools to AI agents.

### Responsibilities
- Initialize FastMCP server with metadata and lifecycle handlers
- Define and register all 14 MCP tools
- Coordinate between MCP protocol and Desktop/Tree layers
- Format responses for protocol compliance

### Key Configurations

```python
pg.FAILSAFE = False    # Disable corner abort
pg.PAUSE = 1.0         # Pause between operations
```

### Server Initialization

```python
@asynccontextmanager
async def lifespan(app: FastMCP):
    await asyncio.sleep(1)  # Startup delay
    yield

mcp = FastMCP(name='windows-mcp', instructions=instructions, lifespan=lifespan)
```

### Tool Registry

| Tool | Function | Parameters | Return Type |
|------|----------|------------|-------------|
| `Launch-Tool` | `launch_tool` | `name: str` | `str` |
| `Powershell-Tool` | `powershell_tool` | `command: str` | `str` |
| `State-Tool` | `state_tool` | `use_vision: bool = False` | `str \| list[str, Image]` |
| `Clipboard-Tool` | `clipboard_tool` | `mode: Literal['copy', 'paste'], text: str = None` | `str` |
| `Click-Tool` | `click_tool` | `loc: tuple[int,int], button: Literal[...], clicks: int` | `str` |
| `Type-Tool` | `type_tool` | `loc: tuple[int,int], text: str, clear: bool` | `str` |
| `Switch-Tool` | `switch_tool` | `name: str` | `str` |
| `Scroll-Tool` | `scroll_tool` | `loc: tuple, type: Literal[...], direction: Literal[...], wheel_times: int` | `str` |
| `Drag-Tool` | `drag_tool` | `from_loc: tuple, to_loc: tuple` | `str` |
| `Move-Tool` | `move_tool` | `to_loc: tuple[int,int]` | `str` |
| `Shortcut-Tool` | `shortcut_tool` | `shortcut: list[str]` | `str` |
| `Key-Tool` | `key_tool` | `key: str` | `str` |
| `Wait-Tool` | `wait_tool` | `duration: int` | `str` |
| `Scrape-Tool` | `scrape_tool` | `url: str` | `str` |

---

## Desktop Class (`src/desktop/__init__.py`)

### Purpose
Primary interface for Windows OS interactions including application management, command execution, and state capture.

### Location
`src/desktop/__init__.py:14`

### Interface

```python
class Desktop:
    def __init__(self) -> None
    def get_state(self, use_vision: bool = False) -> DesktopState
    def get_apps(self) -> list[App]
    def launch_app(self, name: str) -> tuple[str, int]
    def switch_app(self, name: str) -> tuple[str, int]
    def execute_command(self, command: str) -> tuple[str, int]
    def get_screenshot(self, scale: float = 0.7) -> Image.Image
    def get_element_under_cursor(self) -> Control
    def get_taskbar(self) -> Control
    def get_app_status(self, control: Control) -> str
    def get_app_size(self, control: Control) -> Size
    def is_app_visible(self, app: Control) -> bool
    def is_overlay_app(self, element: Control) -> bool
    def screenshot_in_bytes(self, screenshot: Image.Image) -> bytes
    def get_apps_from_start_menu(self) -> dict[str, str]
```

### Key Methods

#### `get_state(use_vision: bool = False) -> DesktopState`
Captures comprehensive desktop state including running applications, UI tree, and optional annotated screenshot.

**Flow:**
1. Create `Tree` instance with `self` reference
2. Get tree state via `tree.get_state()`
3. If `use_vision`, generate annotated screenshot
4. Enumerate running applications
5. Compose and return `DesktopState`

#### `launch_app(name: str) -> tuple[str, int]`
Launches application from Start Menu using fuzzy name matching.

**Flow:**
1. Get Start Menu apps via PowerShell `Get-StartApps`
2. Fuzzy match input name against app list
3. Execute `Start-Process` for matched app
4. Return status tuple

#### `execute_command(command: str) -> tuple[str, int]`
Executes PowerShell command via subprocess.

```python
result = subprocess.run(
    ['powershell', '-Command'] + command.split(),
    capture_output=True, check=True
)
return (result.stdout.decode('latin1'), result.returncode)
```

#### `get_apps() -> list[App]`
Enumerates visible top-level windows.

**Filtering Criteria:**
- Not in `EXCLUDED_APPS`
- Not an overlay app
- Control type is Window or Pane

---

## Tree Class (`src/tree/__init__.py`)

### Purpose
Handles UI Automation accessibility tree traversal and element extraction with parallel processing.

### Location
`src/tree/__init__.py:15`

### Interface

```python
class Tree:
    def __init__(self, desktop: 'Desktop') -> None
    def get_state(self) -> TreeState
    def get_appwise_nodes(self, node: Control) -> tuple[list, list, list]
    def get_nodes(self, node: Control) -> tuple[list, list, list]
    def annotated_screenshot(self, nodes: list, scale: float = 0.7) -> Image.Image
    def get_annotated_image_data(self) -> tuple[Image.Image, list]
```

### Key Methods

#### `get_state() -> TreeState`
Initiates UI tree traversal and returns categorized elements.

```python
def get_state(self) -> TreeState:
    sleep(1.0)  # Allow UI to settle
    root = GetRootControl()
    interactive, informative, scrollable = self.get_appwise_nodes(node=root)
    return TreeState(
        interactive_nodes=interactive,
        informative_nodes=informative,
        scrollable_nodes=scrollable
    )
```

#### `get_appwise_nodes(node: Control) -> tuple`
Parallel traversal of visible application windows.

**Processing:**
1. Filter visible apps (excluding `AVOIDED_APPS`)
2. Always include Taskbar and Program Manager
3. Include foreground app if available
4. Launch parallel `get_nodes()` for each app
5. Aggregate results from all futures

#### `get_nodes(node: Control) -> tuple`
Recursive DFS traversal of single application's UI tree.

**Element Classification:**

| Category | Detection Function | Output Type |
|----------|-------------------|-------------|
| Interactive | `is_element_interactive()` | `TreeElementNode` |
| Informative | `is_element_text()` | `TextElementNode` |
| Scrollable | `is_element_scrollable()` | `ScrollElementNode` |

**DOM Correction Logic (`dom_correction()`):**
- Fixes list items containing links
- Handles unnamed group controls
- Corrects links containing headings

#### `annotated_screenshot(nodes, scale) -> Image.Image`
Generates screenshot with bounding box annotations.

**Process:**
1. Capture desktop screenshot
2. Add padding (20px)
3. For each element: draw colored bounding box + label
4. Use parallel execution for annotation drawing

---

## Data Models (`src/desktop/views.py`)

### DesktopState

```python
@dataclass
class DesktopState:
    apps: list[App]              # Background applications
    active_app: Optional[App]    # Foreground application
    screenshot: bytes | None     # PNG screenshot data
    tree_state: TreeState        # UI element tree
```

**Methods:**
- `active_app_to_string()`: Format active app info
- `apps_to_string()`: Format all apps info

### App

```python
@dataclass
class App:
    name: str                                      # Window title
    depth: int                                     # Z-order depth
    status: Literal['Maximized', 'Minimized', 'Normal']
    size: Size                                     # Window dimensions
    handle: int                                    # Native window handle
```

### Size

```python
@dataclass
class Size:
    width: int
    height: int
```

---

## Data Models (`src/tree/views.py`)

### TreeState

```python
@dataclass
class TreeState:
    interactive_nodes: list[TreeElementNode]
    informative_nodes: list[TextElementNode]
    scrollable_nodes: list[ScrollElementNode]
```

**Methods:**
- `interactive_elements_to_string()`: Format interactive elements
- `informative_elements_to_string()`: Format text elements
- `scrollable_elements_to_string()`: Format scrollable regions

### TreeElementNode

```python
@dataclass
class TreeElementNode:
    name: str              # Element name/label
    control_type: str      # UI Automation control type
    bounding_box: BoundingBox
    shortcut: str          # Accelerator key
    center: Center         # Click coordinates
    app_name: str          # Parent application
```

### TextElementNode

```python
@dataclass
class TextElementNode:
    name: str       # Text content
    app_name: str   # Parent application
```

### ScrollElementNode

```python
@dataclass
class ScrollElementNode:
    name: str
    app_name: str
    center: Center
    bounding_box: BoundingBox
    horizontal_scrollable: bool
    vertical_scrollable: bool
```

### Geometry Types

```python
@dataclass
class Center:
    x: int
    y: int

@dataclass
class BoundingBox:
    left: int
    top: int
    right: int
    bottom: int
```

---

## Configuration Components

### Desktop Configuration (`src/desktop/config.py`)

```python
AVOIDED_APPS: Set[str] = {'Recording toolbar'}
EXCLUDED_APPS: Set[str] = {'Program Manager', 'Taskbar'}.union(AVOIDED_APPS)
```

| Constant | Purpose |
|----------|---------|
| `AVOIDED_APPS` | Apps filtered from tree traversal only |
| `EXCLUDED_APPS` | Apps filtered from `get_apps()` enumeration |

### Tree Configuration (`src/tree/config.py`)

```python
INTERACTIVE_CONTROL_TYPE_NAMES = {
    'ButtonControl', 'ListItemControl', 'MenuItemControl', 'DocumentControl',
    'EditControl', 'CheckBoxControl', 'RadioButtonControl', 'ComboBoxControl',
    'HyperlinkControl', 'SplitButtonControl', 'TabItemControl',
    'TreeItemControl', 'DataItemControl', 'HeaderItemControl', 'TextBoxControl',
    'ImageControl', 'SpinnerControl', 'ScrollBarControl'
}

INFORMATIVE_CONTROL_TYPE_NAMES = {'TextControl', 'ImageControl'}

DEFAULT_ACTIONS = {'Click', 'Press', 'Jump', 'Check', 'Uncheck', 'Double Click'}
```

---

## Utility Components

### Geometry Utilities (`src/tree/utils.py`)

#### `random_point_within_bounding_box(node, scale_factor=1.0) -> tuple[int, int]`

Generates randomized click coordinates within a scaled bounding box.

```python
def random_point_within_bounding_box(node: Control, scale_factor: float = 1.0):
    box = node.BoundingRectangle
    scaled_width = int(box.width() * scale_factor)
    scaled_height = int(box.height() * scale_factor)
    scaled_left = box.left + (box.width() - scaled_width) // 2
    scaled_top = box.top + (box.height() - scaled_height) // 2
    x = random.randint(scaled_left, scaled_left + scaled_width)
    y = random.randint(scaled_top, scaled_top + scaled_height)
    return (x, y)
```

**Purpose:** Avoids predictable click patterns by randomizing click position within element bounds.

---

## Entry Point Components

### Package Entry (`__main__.py`)

```python
from main import mcp

def main():
    mcp.run()

if __name__ == "__main__":
    main()
```

### Console Script Entry (`windows_mcp_entry.py`)

```python
from main import mcp

def main():
    mcp.run()

if __name__ == "__main__":
    main()
```

**Usage:**
- `python -m windows_mcp` → Uses `__main__.py`
- `windows-mcp` (console script) → Uses `windows_mcp_entry.py`

---

## External Dependencies

### uiautomation

Provides Windows UI Automation API access:
- `GetRootControl()`: Get desktop root control
- `Control`: Base class for UI elements
- `GetFocusedControl()`: Get currently focused element
- `WheelUp()`, `WheelDown()`: Scroll operations

### humancursor

Human-like cursor movement simulation:
- `SystemCursor.move_to(loc)`: Move cursor
- `SystemCursor.click_on(loc)`: Click at location
- `SystemCursor.drag_and_drop(from, to)`: Drag operation

### pyautogui

Keyboard and mouse automation:
- `click()`, `mouseDown()`, `mouseUp()`: Mouse operations
- `typewrite()`, `press()`, `hotkey()`: Keyboard operations
- `screenshot()`: Screen capture

### fastmcp

MCP server framework:
- `FastMCP`: Server class
- `@mcp.tool`: Tool registration decorator
- `Image`: Vision response type
