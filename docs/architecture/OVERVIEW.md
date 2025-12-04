# Windows-MCP Overview

## Introduction

Windows-MCP is a lightweight, open-source Model Context Protocol (MCP) server that enables AI agents to interact directly with the Windows operating system. It bridges the gap between Large Language Models (LLMs) and Windows, allowing agents to perform desktop automation tasks including UI interaction, application control, file navigation, and system operations.

## Purpose

The primary goal of Windows-MCP is to provide AI agents with the ability to:

- **Understand Desktop Context**: Capture comprehensive state information about running applications, UI elements, and screen content
- **Interact with UI Elements**: Click, type, scroll, and manipulate interface elements programmatically
- **Control Applications**: Launch, switch between, and manage Windows applications
- **Execute System Commands**: Run PowerShell commands for advanced system operations
- **Support Vision-Based Interaction**: Generate annotated screenshots for visual reasoning

## Key Features

| Feature | Description |
|---------|-------------|
| **Native Windows Integration** | Direct access to Windows UI Automation API via `uiautomation` library |
| **LLM Agnostic** | Works with any LLM without requiring specialized vision models |
| **Accessibility Tree Traversal** | Extracts UI element information through Windows a11y APIs |
| **Human-like Cursor Movement** | Uses `humancursor` for natural mouse movements |
| **Parallel Processing** | Concurrent UI tree traversal for improved performance |
| **Annotated Screenshots** | Visual feedback with labeled bounding boxes |

## Platform Requirements

- **Operating System**: Windows 7, 8, 10, or 11
- **Python Version**: 3.13 or higher
- **Architecture**: x64 (64-bit)

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        AI Agent / LLM                           │
└─────────────────────────────────────────────────────────────────┘
                                │
                         MCP Protocol
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Windows-MCP Server                          │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │                    FastMCP Framework                        ││
│  │              (Tool Definitions & Protocol)                  ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │                    Desktop Layer                            ││
│  │    (App Management, State Capture, PowerShell Execution)    ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │                      Tree Layer                             ││
│  │      (UI Tree Traversal, Element Extraction, Annotation)    ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Windows Operating System                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │  UI Automation│  │  PowerShell  │  │  Desktop/Applications│  │
│  │      API      │  │    Engine    │  │                      │  │
│  └──────────────┘  └──────────────┘  └──────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## Available Tools

Windows-MCP exposes 14 tools through the MCP protocol:

### Desktop State Tools
| Tool | Purpose |
|------|---------|
| `State-Tool` | Capture comprehensive desktop state (apps, UI elements, optional screenshot) |
| `Screenshot-Tool` | Capture desktop screenshot |

### Application Control Tools
| Tool | Purpose |
|------|---------|
| `Launch-Tool` | Launch applications from Start Menu |
| `Switch-Tool` | Switch to running application window |

### Mouse Interaction Tools
| Tool | Purpose |
|------|---------|
| `Click-Tool` | Click at coordinates (left/right/middle, single/double/triple) |
| `Move-Tool` | Move cursor to coordinates |
| `Drag-Tool` | Drag from source to destination |
| `Scroll-Tool` | Scroll vertically/horizontally |

### Keyboard Interaction Tools
| Tool | Purpose |
|------|---------|
| `Type-Tool` | Type text into focused element |
| `Key-Tool` | Press individual keys |
| `Shortcut-Tool` | Execute keyboard shortcuts |
| `Clipboard-Tool` | Copy/paste via system clipboard |

### Utility Tools
| Tool | Purpose |
|------|---------|
| `Wait-Tool` | Pause execution for specified duration |
| `Powershell-Tool` | Execute PowerShell commands |
| `Scrape-Tool` | Fetch and convert webpage to markdown |

## Performance Characteristics

- **State Capture Latency**: 1.5 - 2.3 seconds (varies with application count)
- **PyAutoGUI Pause**: 1.0 second between operations (configurable)
- **Tree Traversal**: Parallel across applications via ThreadPoolExecutor
- **Screenshot Annotation**: Concurrent label drawing

## Core Dependencies

| Package | Purpose |
|---------|---------|
| `fastmcp` | MCP server framework and protocol handling |
| `uiautomation` | Windows UI Automation API access |
| `pyautogui` | Keyboard and mouse control |
| `humancursor` | Human-like cursor movement simulation |
| `pillow` | Image processing for screenshots |
| `fuzzywuzzy` | Fuzzy string matching for app names |

## Use Cases

1. **Desktop Automation**: Automate repetitive Windows tasks
2. **UI Testing**: Automated UI testing and QA workflows
3. **Accessibility Analysis**: Extract UI element information for accessibility auditing
4. **AI Agent Development**: Enable LLM agents to interact with Windows applications
5. **RPA (Robotic Process Automation)**: Business process automation through AI
