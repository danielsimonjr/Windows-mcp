# Windows-MCP Local Installation Guide

This guide explains how to install Windows-MCP as a pip package for local or system-wide use.

## Package Information

- **Package Name**: `windows-mcp`
- **Version**: 0.1.0
- **Platform**: Windows only (win32)
- **Python Version**: 3.13+

## Installation Methods

### Method 1: Install from Local Wheel (Recommended)

The package has been pre-built and is available in the `dist/` directory.

```bash
# Install from the wheel file (faster)
pip install dist/windows_mcp-0.1.0-py3-none-any.whl

# OR install from source distribution
pip install dist/windows_mcp-0.1.0.tar.gz
```

### Method 2: Install in Development/Editable Mode

For development or testing, install in editable mode:

```bash
# From the package directory
pip install -e .
```

This creates a link to the source code, so changes take effect immediately without reinstalling.

### Method 3: Install from Source Directory

```bash
# From the package directory
pip install .
```

## Building the Package

If you need to rebuild the package:

```bash
# Install build tool
pip install build

# Build the package (creates wheel and source distribution)
python -m build
```

This creates:
- `dist/windows_mcp-0.1.0-py3-none-any.whl` (wheel - recommended)
- `dist/windows_mcp-0.1.0.tar.gz` (source distribution)

## Usage After Installation

Once installed, you can run the MCP server using:

```bash
# Run the server
windows-mcp
```

Or use it as a Python module:

```python
from main import mcp
mcp.run()
```

## Configuration for Claude Desktop

After installing via pip, update your `claude_desktop_config.json`:

### Option 1: Use Installed Package (System Python)

```json
{
  "mcpServers": {
    "windows-mcp": {
      "type": "stdio",
      "command": "python",
      "args": ["-m", "windows_mcp"],
      "env": {}
    }
  }
}
```

### Option 2: Use Specific Python Path

```json
{
  "mcpServers": {
    "windows-mcp": {
      "type": "stdio",
      "command": "C:\\Users\\YourUsername\\AppData\\Local\\Programs\\Python\\Python313\\python.exe",
      "args": ["-m", "windows_mcp"],
      "env": {}
    }
  }
}
```

### Option 3: Use Console Script Entry Point

```json
{
  "mcpServers": {
    "windows-mcp": {
      "type": "stdio",
      "command": "windows-mcp",
      "args": [],
      "env": {}
    }
  }
}
```

## Dependencies

The package will automatically install these dependencies:

- fastmcp >= 2.8.1
- fuzzywuzzy >= 0.18.0
- humancursor >= 1.1.5
- live-inspect >= 0.1.1
- markdownify >= 1.1.0
- pillow >= 11.2.1
- pyautogui >= 0.9.54
- python-levenshtein >= 0.27.1
- requests >= 2.32.3
- uiautomation >= 2.0.24
- pyperclip >= 1.8.2

**Note**: Some dependencies like `pythonnet` may have version compatibility issues with Python 3.14. It's recommended to use Python 3.13 for best compatibility.

## Troubleshooting

### Dependency Installation Issues

If you encounter dependency issues (especially with `pythonnet`), you can:

1. **Use Python 3.13** instead of 3.14
2. **Install with existing venv**: Use the pre-configured `.venv` in the extension directory
3. **Install dependencies separately**: Install problematic dependencies manually first

### Running the MCP Server

To test if the server runs correctly:

```bash
# This should start the MCP server
windows-mcp
```

You should see output like:
```
INFO     Starting MCP server 'windows-mcp' with transport 'stdio'
```

## Uninstalling

To uninstall the package:

```bash
pip uninstall windows-mcp
```

## Publishing to PyPI (Optional)

To publish this package to PyPI for public distribution:

```bash
# Install twine
pip install twine

# Upload to PyPI
python -m twine upload dist/*
```

You'll need PyPI credentials to publish.

## Package Structure

```
windows-mcp/
├── main.py              # Main MCP server code
├── __main__.py          # Entry point for module execution
├── src/                 # Source package
│   ├── desktop/         # Desktop interaction modules
│   └── tree/            # UI tree modules
├── pyproject.toml       # Package configuration
├── MANIFEST.in          # Files to include in distribution
├── LICENSE              # MIT License
├── README.md            # Project documentation
└── dist/                # Built packages (after running build)
    ├── windows_mcp-0.1.0-py3-none-any.whl
    └── windows_mcp-0.1.0.tar.gz
```

## Benefits of Pip Installation

✅ **Faster startup**: No UV runtime overhead
✅ **System-wide access**: Available from any terminal
✅ **Dependency management**: Automatic dependency installation
✅ **Easy updates**: Simple `pip install --upgrade`
✅ **Standard Python packaging**: Works with all Python tools
✅ **Virtual environment support**: Can be installed in any venv

## Original Installation Method

This package can still be installed as a Claude Desktop extension using UV:

```json
{
  "mcpServers": {
    "windows-mcp": {
      "type": "stdio",
      "command": "uv",
      "args": ["--directory", "path/to/extension", "run", "main.py"],
      "env": {}
    }
  }
}
```

The pip installation method provides a faster, more standard Python experience.
