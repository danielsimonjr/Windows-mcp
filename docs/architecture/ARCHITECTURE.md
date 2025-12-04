# Windows-MCP System Architecture

## Architectural Overview

Windows-MCP follows a layered architecture pattern with clear separation of concerns. The system is organized into three primary layers: the MCP Interface Layer, the Desktop Management Layer, and the UI Tree Layer.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          MCP Interface Layer                                │
│                            (main.py)                                        │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                         FastMCP Server                                 │ │
│  │  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐ │ │
│  │  │State-Tool│ │Click-Tool│ │Type-Tool │ │Launch-Tool│ │...10 more   │ │ │
│  │  └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────────┘ │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                      ┌─────────────┴──────────────┐
                      ▼                            ▼
┌─────────────────────────────────┐  ┌─────────────────────────────────┐
│   Desktop Management Layer      │  │       UI Tree Layer             │
│   (src/desktop/)                │  │       (src/tree/)               │
│  ┌───────────────────────────┐  │  │  ┌───────────────────────────┐  │
│  │       Desktop Class       │  │  │  │       Tree Class          │  │
│  │                           │  │  │  │                           │  │
│  │  • get_state()            │──┼──┼──│  • get_state()            │  │
│  │  • get_apps()             │  │  │  │  • get_appwise_nodes()    │  │
│  │  • launch_app()           │  │  │  │  • get_nodes()            │  │
│  │  • switch_app()           │  │  │  │  • annotated_screenshot() │  │
│  │  • execute_command()      │  │  │  │                           │  │
│  │  • get_screenshot()       │  │  │  └───────────────────────────┘  │
│  └───────────────────────────┘  │  │              │                  │
│              │                  │  │              ▼                  │
│  ┌───────────────────────────┐  │  │  ┌───────────────────────────┐  │
│  │     Data Models           │  │  │  │     Data Models           │  │
│  │  (views.py)               │  │  │  │  (views.py)               │  │
│  │  • DesktopState           │  │  │  │  • TreeState              │  │
│  │  • App                    │  │  │  │  • TreeElementNode        │  │
│  │  • Size                   │  │  │  │  • TextElementNode        │  │
│  └───────────────────────────┘  │  │  │  • ScrollElementNode      │  │
│              │                  │  │  │  • Center, BoundingBox    │  │
│  ┌───────────────────────────┐  │  │  └───────────────────────────┘  │
│  │  Configuration (config.py)│  │  │  ┌───────────────────────────┐  │
│  │  • EXCLUDED_APPS          │  │  │  │  Configuration (config.py)│  │
│  │  • AVOIDED_APPS           │  │  │  │  • INTERACTIVE_CONTROL_*  │  │
│  └───────────────────────────┘  │  │  │  • INFORMATIVE_CONTROL_*  │  │
└─────────────────────────────────┘  │  │  • DEFAULT_ACTIONS         │  │
                                     │  └───────────────────────────┘  │
                                     │  ┌───────────────────────────┐  │
                                     │  │  Utilities (utils.py)     │  │
                                     │  │  • random_point_within_*  │  │
                                     │  └───────────────────────────┘  │
                                     └─────────────────────────────────┘
                                                    │
                                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Windows Platform Layer                               │
│  ┌───────────────────┐  ┌────────────────┐  ┌────────────────────────────┐ │
│  │  UI Automation    │  │   PowerShell   │  │      Win32 APIs            │ │
│  │  (uiautomation)   │  │   (subprocess) │  │  (pyautogui, humancursor)  │ │
│  └───────────────────┘  └────────────────┘  └────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Layer Descriptions

### 1. MCP Interface Layer (`main.py`)

The top layer serves as the protocol interface between AI agents and the Windows automation functionality. Built on FastMCP framework, it:

- Defines and registers 14 MCP tools with decorators (`@mcp.tool`)
- Manages the server lifecycle with async context manager
- Coordinates between user input and lower layer operations
- Handles response formatting for MCP protocol compliance

**Key Configuration:**
```python
pg.FAILSAFE = False  # Disable fail-safe corner abort
pg.PAUSE = 1.0       # 1-second delay between operations
```

### 2. Desktop Management Layer (`src/desktop/`)

The middle layer manages application-level interactions with Windows:

| Component | Responsibility |
|-----------|---------------|
| `Desktop` class | Primary interface for Windows operations |
| `DesktopState` | Composite state container (apps + tree + screenshot) |
| `App` dataclass | Individual application metadata |
| Configuration | App filtering rules (excluded/avoided apps) |

**Core Capabilities:**
- Application enumeration and status detection
- PowerShell command execution
- Start Menu app launching with fuzzy matching
- Window management and foreground control
- Screenshot capture and processing

### 3. UI Tree Layer (`src/tree/`)

The bottom application layer handles UI Automation accessibility tree processing:

| Component | Responsibility |
|-----------|---------------|
| `Tree` class | A11y tree traversal and element extraction |
| `TreeState` | Container for categorized UI elements |
| `TreeElementNode` | Interactive element representation |
| `TextElementNode` | Informative text element |
| `ScrollElementNode` | Scrollable region metadata |

**Element Categorization:**
- **Interactive**: Buttons, links, text fields, checkboxes, etc.
- **Informative**: Static text, labels, status indicators
- **Scrollable**: Elements with scroll patterns

### 4. Windows Platform Layer

External dependencies providing low-level Windows access:

| Library | Purpose |
|---------|---------|
| `uiautomation` | Windows UI Automation API wrapper |
| `pyautogui` | Keyboard/mouse simulation |
| `humancursor` | Natural cursor movement |
| `subprocess` | PowerShell command execution |

## Design Patterns

### 1. Singleton-like Instantiation

The `Desktop` and `SystemCursor` objects are instantiated once at module level:

```python
desktop = Desktop()
cursor = SystemCursor()
mcp = FastMCP(name='windows-mcp', ...)
```

### 2. Dataclass-Based Data Transfer Objects

All state representations use Python dataclasses for type safety and immutability:

```python
@dataclass
class TreeElementNode:
    name: str
    control_type: str
    bounding_box: BoundingBox
    shortcut: str
    center: Center
    app_name: str
```

### 3. Parallel Processing with ThreadPoolExecutor

UI tree traversal leverages concurrent execution for performance:

```python
with ThreadPoolExecutor() as executor:
    future_to_node = {executor.submit(self.get_nodes, app): app
                      for app in apps.values()}
    for future in as_completed(future_to_node):
        result = future.result()
```

### 4. Composite State Pattern

`DesktopState` aggregates multiple state sources:

```python
@dataclass
class DesktopState:
    apps: list[App]
    active_app: Optional[App]
    screenshot: bytes | None
    tree_state: TreeState
```

### 5. Fuzzy Matching Strategy

Application and window matching uses fuzzy string comparison:

```python
matched_app = process.extractOne(name, apps_map.keys())
```

## Module Dependencies

```
main.py
├── fastmcp (FastMCP, Image)
├── humancursor (SystemCursor)
├── pyautogui
├── uiautomation
├── pyperclip
├── requests, markdownify
└── src.desktop
    ├── Desktop (src/desktop/__init__.py)
    └── src.tree
        ├── Tree (src/tree/__init__.py)
        ├── config.py (control type definitions)
        ├── views.py (data models)
        └── utils.py (geometry helpers)
```

## Entry Points

The server supports multiple entry points:

| Entry Point | Usage |
|-------------|-------|
| `python main.py` | Direct execution |
| `python -m windows_mcp` | Module execution (via `__main__.py`) |
| `windows-mcp` | Console script (via `windows_mcp_entry.py`) |

## Configuration Architecture

### App Filtering Configuration

```python
# src/desktop/config.py
AVOIDED_APPS = {'Recording toolbar'}
EXCLUDED_APPS = {'Program Manager', 'Taskbar'}.union(AVOIDED_APPS)
```

### Control Type Configuration

```python
# src/tree/config.py
INTERACTIVE_CONTROL_TYPE_NAMES = {
    'ButtonControl', 'EditControl', 'CheckBoxControl', ...
}
INFORMATIVE_CONTROL_TYPE_NAMES = {
    'TextControl', 'ImageControl'
}
DEFAULT_ACTIONS = {
    'Click', 'Press', 'Jump', 'Check', 'Uncheck', 'Double Click'
}
```

## Error Handling Strategy

1. **Graceful Degradation**: Methods return tuples with status codes rather than raising exceptions
2. **Silent Failures in Traversal**: Tree traversal catches exceptions per-element to prevent cascade failures
3. **Process Isolation**: PowerShell commands run in subprocess with captured output
4. **Status Code Returns**: Tool methods return descriptive strings with success/failure context

## Security Considerations

1. **No FAILSAFE**: `pg.FAILSAFE = False` disables the mouse corner abort feature
2. **PowerShell Execution**: Direct command execution requires trust in input sources
3. **UI Automation Access**: Requires appropriate Windows permissions
4. **No Input Sanitization**: Tool parameters are passed directly to system APIs
