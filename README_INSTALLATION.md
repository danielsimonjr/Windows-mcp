# Windows-MCP Installation in C:/mcp-servers/

This Windows-MCP installation has been cloned from the Claude Desktop extension directory and is ready for use.

## Location

`C:/mcp-servers/Windows-MCP/`

## What's Included

All files from the original extension plus packaging enhancements:

- ✅ **Main server code**: `main.py`, `__main__.py`
- ✅ **Source package**: `src/` directory with desktop and tree modules
- ✅ **Built packages**: `dist/` with wheel and tar.gz
- ✅ **Documentation**: README.md, INSTALL.md, PACKAGING_SUMMARY.md
- ✅ **Configuration**: pyproject.toml, manifest.json, MANIFEST.in
- ✅ **Virtual environment**: `.venv/` (with all dependencies pre-installed)
- ✅ **Assets**: `assets/` directory with icons and screenshots

## Quick Start

### Option 1: Run Directly from This Directory

```bash
cd C:/mcp-servers/Windows-MCP
python main.py
```

### Option 2: Install via Pip

```bash
# Install from wheel (recommended)
pip install C:/mcp-servers/Windows-MCP/dist/windows_mcp-0.1.0-py3-none-any.whl

# Or install from source
cd C:/mcp-servers/Windows-MCP
pip install .
```

### Option 3: Use Existing Virtual Environment

```bash
# The .venv directory already has all dependencies installed
cd C:/mcp-servers/Windows-MCP
.venv/Scripts/python.exe main.py
```

## Claude Desktop Configuration

### Using This Installation

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "windows-mcp": {
      "type": "stdio",
      "command": "C:/mcp-servers/Windows-MCP/.venv/Scripts/python.exe",
      "args": ["C:/mcp-servers/Windows-MCP/main.py"],
      "env": {}
    }
  }
}
```

### After Pip Installation

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

## Testing

Verify the installation works:

```bash
cd C:/mcp-servers/Windows-MCP
C:/Users/danie/AppData/Roaming/Claude/Claude\ Extensions/ant.dir.cursortouch.windows-mcp/.venv/Scripts/python.exe main.py
```

You should see:
```
INFO     Starting MCP server 'windows-mcp' with transport 'stdio'
```

## Package Details

- **Package Name**: windows-mcp
- **Version**: 0.1.0
- **Python Required**: 3.13+
- **Platform**: Windows only
- **License**: MIT

## File Structure

```
Windows-MCP/
├── main.py                    # Main MCP server
├── __main__.py               # Module entry point
├── src/                      # Source package
│   ├── desktop/             # Desktop automation
│   └── tree/                # UI tree handling
├── dist/                    # Built packages
│   ├── windows_mcp-0.1.0-py3-none-any.whl
│   └── windows_mcp-0.1.0.tar.gz
├── .venv/                   # Virtual environment (pre-configured)
├── assets/                  # Icons and screenshots
├── pyproject.toml          # Package configuration
├── manifest.json           # Extension manifest
├── INSTALL.md             # Installation guide
├── PACKAGING_SUMMARY.md   # Packaging details
└── README.md              # Project documentation
```

## Documentation

- **INSTALL.md** - Complete installation guide with all methods
- **PACKAGING_SUMMARY.md** - Technical packaging details
- **README.md** - Project overview and features
- **CONTRIBUTING.md** - Contribution guidelines

## Next Steps

1. **Test the server** - Run it to verify all tools work
2. **Install via pip** - For system-wide access
3. **Update Claude Desktop config** - Point to this installation
4. **Restart Claude Desktop** - For changes to take effect

## Advantages of This Setup

✅ **Centralized location** - All MCP servers in one place
✅ **Pre-built packages** - Ready to install via pip
✅ **Complete documentation** - All guides included
✅ **Virtual environment** - Dependencies already installed
✅ **Easy updates** - Simply rebuild the package

This installation is fully functional and ready to use! 🎉
