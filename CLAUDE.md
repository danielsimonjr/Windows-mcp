# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Windows-MCP is a lightweight, open-source MCP (Model Context Protocol) server that enables AI agents to interact directly with the Windows operating system. It provides tools for UI automation, desktop interaction, application control, and system operations through a11y (accessibility) tree traversal.

**Version**: 0.1.0 | **Platform**: Windows 7-11 only | **Python**: 3.13+ | **Entry Point**: `main.py`

## Project Structure

```
Windows-MCP/
├── main.py              # MCP server entry point with 14 tool definitions
├── __main__.py          # Package entry point for pip installation
├── src/
│   ├── desktop/         # Desktop state management and app control
│   │   ├── __init__.py  # Desktop class - core Windows interaction layer
│   │   ├── config.py    # App filtering configuration (EXCLUDED_APPS, AVOIDED_APPS)
│   │   └── views.py     # Data models (DesktopState, App, Size)
│   └── tree/            # UI element tree traversal and extraction
│       ├── __init__.py  # Tree class - a11y tree traversal and screenshot annotation
│       ├── config.py    # UI element type configuration (INTERACTIVE_CONTROL_TYPE_NAMES, etc.)
│       ├── views.py     # Tree data models (TreeElementNode, TextElementNode, ScrollElementNode)
│       └── utils.py     # Geometry utilities for bounding box operations
├── pyproject.toml       # Package configuration and dependencies
├── manifest.json        # DXT (Desktop Extension) metadata
├── dist/                # Built wheel and source distributions
└── .venv/               # Virtual environment (pre-configured)
```

## Architecture

### Core Components

**Desktop Layer (`src/desktop/__init__.py`)**
- `Desktop` class: Primary interface to Windows OS
- Manages application state, screenshots, PowerShell command execution
- Uses `uiautomation` library for Windows UI Automation API access
- Key methods:
  - `get_state()`: Captures comprehensive desktop state (apps + UI tree + optional screenshot)
  - `launch_app()`: Launches apps from Start Menu using PowerShell + fuzzy matching
  - `switch_app()`: Brings windows to foreground
  - `execute_command()`: Executes PowerShell commands via subprocess

**Tree Layer (`src/tree/__init__.py`)**
- `Tree` class: UI accessibility tree traversal and element extraction
- Parallel traversal with `ThreadPoolExecutor` for performance
- Categorizes elements into three types:
  - **Interactive**: Buttons, links, text fields, checkboxes (defined in `INTERACTIVE_CONTROL_TYPE_NAMES`)
  - **Informative**: Static text, labels (defined in `INFORMATIVE_CONTROL_TYPE_NAMES`)
  - **Scrollable**: Elements with scroll patterns
- `annotated_screenshot()`: Generates annotated screenshots with bounding boxes and labels
- DOM correction logic to fix a11y tree inconsistencies (e.g., nested links, unnamed groups)

**Tool Definitions (`main.py`)**
- 14 MCP tools exposed via FastMCP decorators (`@mcp.tool`)
- Tools use `humancursor` for mouse operations and `pyautogui` for keyboard input
- `State-Tool` is the primary context-gathering tool (returns apps + UI elements + optional screenshot)

## Build & Development Commands

### Running the Server

```bash
# Direct execution (development)
python main.py

# Using uv (as Claude Desktop extension)
uv --directory C:\mcp-servers\Windows-MCP run main.py

# Using pip-installed package
windows-mcp
```

### Building the Package

```bash
# Install build tool
pip install build

# Build wheel and source distribution
python -m build

# Output: dist/windows_mcp-0.1.0-py3-none-any.whl
#         dist/windows_mcp-0.1.0.tar.gz
```

### Installation Methods

```bash
# Install from wheel (fastest, recommended)
pip install dist/windows_mcp-0.1.0-py3-none-any.whl

# Editable install for development
pip install -e .

# Standard install from current directory
pip install .
```

### Building DXT Extension

```bash
# Pack as Claude Desktop extension
npx @anthropic-ai/dxt pack

# Output: Windows-MCP.dxt
```

### Testing

```bash
# Run the server in test mode
python main.py

# Test with MCP inspector (if available)
npx @modelcontextprotocol/inspector python main.py
```

## Configuration Files

### .mcp.json (Project-level MCP Server Registry)

The `.mcp.json` file in the project root configures all available MCP servers for this workspace. This includes the Windows-MCP server along with other servers in the C:/mcp-servers/ collection.

**Location**: `C:\mcp-servers\Windows-MCP\.mcp.json`

This file is used by Claude Code to auto-discover and connect to MCP servers when working in this directory.

### .claude/settings.local.json (Claude Code Permissions)

The `.claude/settings.local.json` file configures Claude Code permissions for this project:

**Location**: `C:\mcp-servers\Windows-MCP\.claude\settings.local.json`

**Key Settings:**
- `permissions.allow`: Pre-approved Bash commands and MCP tool calls
- `enableAllProjectMcpServers`: Automatically enable all servers from .mcp.json
- `enabledMcpjsonServers`: List of specific servers to enable

**Pre-approved Windows-MCP Tools:**
- All 14 Windows automation tools (Launch-Tool, State-Tool, Click-Tool, etc.)
- Memory-MCP tools for cross-session context
- DeepThinking-MCP tools for complex reasoning
- Everything-MCP for file search
- Git commands, Python/pip/uv commands, build commands

## MCP Server Configuration

### Claude Desktop (using UV)

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "windows-mcp": {
      "command": "uv",
      "args": [
        "--directory",
        "C:\\mcp-servers\\Windows-MCP",
        "run",
        "main.py"
      ]
    }
  }
}
```

### Claude Desktop (using pip-installed package)

```json
{
  "mcpServers": {
    "windows-mcp": {
      "command": "python",
      "args": ["-m", "windows_mcp"]
    }
  }
}
```

### Gemini CLI

Add to `%USERPROFILE%\.gemini\settings.json`:

```json
{
  "mcpServers": {
    "windows-mcp": {
      "command": "uv",
      "args": [
        "--directory",
        "C:\\mcp-servers\\Windows-MCP",
        "run",
        "main.py"
      ]
    }
  }
}
```

## Available MCP Tools

The server exposes 14 tools for Windows interaction:

**Desktop State**
- `State-Tool`: Capture desktop state (apps, UI elements, optional screenshot)
- `Screenshot-Tool`: Take desktop screenshot

**Application Control**
- `Launch-Tool`: Launch apps from Start Menu by name
- `Switch-Tool`: Switch to running application window

**Mouse Operations** (using `humancursor` library)
- `Click-Tool`: Click at coordinates (left/right/middle, single/double/triple)
- `Move-Tool`: Move mouse cursor to coordinates
- `Drag-Tool`: Drag from source to destination coordinates
- `Scroll-Tool`: Scroll vertically/horizontally at coordinates

**Keyboard Operations** (using `pyautogui` library)
- `Type-Tool`: Type text into focused element
- `Key-Tool`: Press individual keys (including special keys)
- `Shortcut-Tool`: Execute keyboard shortcuts (e.g., ["ctrl", "c"])
- `Clipboard-Tool`: Copy/paste using system clipboard

**Utility**
- `Wait-Tool`: Pause execution for specified duration
- `Powershell-Tool`: Execute PowerShell commands
- `Scrape-Tool`: Fetch webpage content and convert to markdown

## Key Technical Details

### PyAutoGUI Configuration
```python
pg.FAILSAFE = False  # Disables fail-safe corner abort
pg.PAUSE = 1.0       # 1-second delay between operations
```

### Element Visibility Logic
Elements must meet all criteria to be considered interactive:
- `IsControlElement == True`
- `IsOffscreen == False`
- `IsEnabled == True`
- Bounding box area > 0
- Not an unlabeled image control

### App Filtering
- `EXCLUDED_APPS`: Filtered from `get_apps()` (Program Manager, Taskbar)
- `AVOIDED_APPS`: Filtered from tree traversal (Recording toolbar)

### Coordinate System
- All coordinates use center points of bounding boxes
- `random_point_within_bounding_box()` generates randomized click points (0.5 scale factor)
- Screenshot annotation scales coordinates by configurable factor (default 0.7)

### Parallel Processing
- App tree traversal uses `ThreadPoolExecutor` for concurrent element extraction
- Screenshot annotation draws labels in parallel

## Dependencies

**Core MCP Framework**
- `fastmcp>=2.8.1` - FastMCP server framework

**Windows Automation**
- `uiautomation>=2.0.24` - Windows UI Automation API
- `pyautogui>=0.9.54` - Keyboard/mouse control
- `humancursor>=1.1.5` - Human-like cursor movements

**Utilities**
- `fuzzywuzzy>=0.18.0` + `python-levenshtein>=0.27.1` - Fuzzy app name matching
- `pillow>=11.2.1` - Image processing for screenshots
- `pyperclip>=1.8.2` - Clipboard operations
- `markdownify>=1.1.0` - HTML to markdown conversion
- `requests>=2.32.3` - HTTP requests for web scraping
- `live-inspect>=0.1.1` - Runtime inspection

**Note**: `pythonnet` (indirect dependency) has compatibility issues with Python 3.14+. Use Python 3.13 for best results.

## Development Notes

### Adding New Tools

1. Define tool function in `main.py` with `@mcp.tool()` decorator
2. Add type hints for all parameters
3. Return `str` or `list[str | Image]` for vision support
4. Update `manifest.json` tools array for DXT metadata
5. Rebuild package if distributing: `python -m build`

### Modifying UI Element Detection

1. Edit control type sets in `src/tree/config.py`:
   - `INTERACTIVE_CONTROL_TYPE_NAMES`: Clickable/focusable elements
   - `INFORMATIVE_CONTROL_TYPE_NAMES`: Text-only elements
   - `DEFAULT_ACTIONS`: Legacy IAccessible default actions
2. Modify filtering logic in `Tree.get_nodes()` (`src/tree/__init__.py`)
3. DOM correction logic handles a11y tree quirks (list items with child links, unnamed groups, etc.)

### Desktop State Workflow

```
Desktop.get_state(use_vision=False)
  └─> Tree.get_state()
       └─> Tree.get_appwise_nodes()  [parallel]
            └─> Tree.get_nodes()  [per app, parallel]
                 └─> tree_traversal()  [recursive DFS]
```

### Performance Characteristics

- **State-Tool latency**: 1.5-2.3 seconds (varies with app count)
- **Element extraction**: Parallel across apps (ThreadPoolExecutor)
- **Screenshot annotation**: Parallel label drawing
- **Sleep delays**: 0.75s before app enumeration, 1.0s before tree traversal, 0.25s after screenshot

## Important Behavioral Notes

1. **Coordinate Selection**: `Type-Tool` and `Click-Tool` require coordinates from `State-Tool` output
2. **App Matching**: Fuzzy matching allows approximate app names (e.g., "chrome" matches "Google Chrome")
3. **Clear Parameter**: `Type-Tool` accepts `clear` parameter but checks as string `'True'` (potential bug)
4. **Screenshot Scaling**: Vision mode screenshots are scaled to 50% by default
5. **PowerShell Encoding**: Output decoded as `latin1` (line 62, `main.py:60`)
6. **Element Under Cursor**: Returns focused control, not actual element under cursor coordinates

## Claude Desktop Extension (DXT)

The project includes `manifest.json` for packaging as a `.dxt` file installable in Claude Desktop:
- `npx @anthropic-ai/dxt pack` creates the extension bundle
- Extension includes embedded `.venv` with Python interpreter
- `manifest.json` specifies entry point as `.venv/Scripts/python.exe main.py`

## Known Limitations

1. Cannot select specific text sections within paragraphs (a11y tree limitation)
2. `Type-Tool` types entire program files at once (not suitable for IDE coding)
3. Windows-only (relies on Windows UI Automation API)
4. Requires `es.exe` (Everything search) for some features - **Note**: This appears in README but not in actual code

## Code Style (from CONTRIBUTING.md)

- **Formatter/Linter**: Ruff (configured in `ruff.toml` if present)
- **Line length**: 100 characters
- **Quotes**: Double quotes for strings
- **Type hints**: Required on function signatures
- **Docstrings**: Google-style format
- **Pre-commit hooks**: Available (run `pre-commit install`)

## Development Best Practices

### Cleanup Before Committing

**CRITICAL**: Always remove temporary debug/test artifacts before committing:

```bash
# Before git commit - check for junk files
git status

# Common temporary files to remove:
rm test-*.py debug-*.py temp-*.py .error.txt

# Verify only intended files are staged
git diff --cached
```

**Cleanup Checklist:**
- ✅ Remove temporary test scripts (e.g., `test-server.py`, `debug-tools.py`)
- ✅ Delete debug files created during troubleshooting
- ✅ Check for `.error.txt` or other runtime artifacts
- ✅ Review `git status` output before committing
- ✅ Verify `dist/` doesn't contain test artifacts before publishing
- ✅ Remove any hardcoded paths or credentials

**Workflow:**
1. Create temp files for debugging → Test/Debug → **Delete temp files** → Commit clean code
2. Before `git commit`: Ask yourself "Did I create any temp/debug files?"
3. Before publishing: Verify package contents are clean

### Build & Publish Workflow

For distributing the package:

```bash
# Correct workflow
1. Make source changes in src/ or main.py
2. Test changes: python main.py
3. python -m build              # Build package
4. git add -A && git commit     # Commit source + dist
5. # Optionally publish to PyPI
6. git push origin main         # Push to GitHub
```

## Entry Points

- **CLI execution**: `python main.py`
- **Package module**: `python -m windows_mcp` (after pip install)
- **Console script**: `windows-mcp` (after pip install)
- **Source entry**: `main.py` (FastMCP server with tool definitions)

## Memory Usage Reminder

**IMPORTANT**: Use the `memory-mcp` tools periodically during sessions to maintain cross-session context:

1. **At session start**: Search for existing context with `mcp__memory-mcp__search_nodes` using query "windows-mcp" or "Windows MCP"
2. **During work**: Add observations for significant changes with `mcp__memory-mcp__add_observations`
   - Record bug fixes, new features, API changes
   - Note architectural decisions and their rationale
   - Document known issues or workarounds
3. **At session end**: Update memory with session summary and next steps
   - Summarize what was accomplished
   - Record unfinished tasks or blockers
   - Note user preferences or patterns observed

**Entity**: "Windows MCP" (importance: 10, tags: mcp, windows, automation, python, fastmcp, active-project)
