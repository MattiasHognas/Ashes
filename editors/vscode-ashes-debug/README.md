# Ashes Debug — VS Code Extension

Debug Adapter Protocol (DAP) extension for debugging Ashes programs in VS Code.

## Prerequisites

1. **Compile with debug info**: `ashes compile --debug your-program.ash`
2. **GDB** must be installed and on `PATH` (or configure `debuggerPath`)
3. **ashes-dap** server must be installed and on `PATH` (or configure `dapServerPath`)

## Usage

### Quick Start

1. Open an Ashes project in VS Code
2. Compile with `--debug` flag: `ashes compile --debug src/main.ash`
3. Set breakpoints in `.ash` files
4. Press **F5** or use **Run > Start Debugging**

### launch.json Configuration

Create a `.vscode/launch.json`:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Ashes: Launch",
      "type": "ashes",
      "request": "launch",
      "program": "${workspaceFolder}/out/${workspaceFolderBasename}",
      "cwd": "${workspaceFolder}",
      "stopOnEntry": false
    }
  ]
}
```

### Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `program` | string | *required* | Path to the compiled binary (compiled with `--debug`) |
| `args` | string[] | `[]` | Arguments to pass to the program |
| `cwd` | string | `${workspaceFolder}` | Working directory |
| `stopOnEntry` | boolean | `false` | Stop at the entry point |
| `debuggerPath` | string | `"gdb"` | Path to GDB |
| `dapServerPath` | string | `"ashes-dap"` | Path to the DAP server binary |

## Development

```bash
cd editors/vscode-ashes-debug
npm install
npm run build
```

To test locally, press **F5** in VS Code from this directory to launch an Extension Development Host.

## Architecture

```
VS Code  ←—DAP over stdio—→  ashes-dap  ←—GDB/MI—→  GDB  ←—DWARF—→  binary
```

The extension launches `ashes-dap` as a child process, communicating over stdin/stdout using the Debug Adapter Protocol. The DAP server in turn drives GDB via its Machine Interface (MI) protocol to set breakpoints, step through code, and inspect variables.
