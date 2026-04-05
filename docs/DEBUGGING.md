# Debugging Ashes Programs

This document covers how to compile Ashes programs with debug information,
set up the VS Code debug extension, and use GDB or LLDB for source-level
debugging.

> **Supported platforms:**
> - **Linux** — GDB (default) or LLDB via `lldb-mi`
> - **Windows** — GDB via MSYS2
> - **macOS** — LLDB via `lldb-mi` (recommended) or GDB (requires code-signing)
>
> The Ashes DAP server uses the GDB Machine Interface (MI) protocol.
> Both GDB and LLDB-MI implement this protocol, so the same adapter works
> with either debugger.

> **Related documents:**
> - [CLI Specification — Debug Mode](COMPILER_CLI_SPEC.md#debug-mode) for the
>   `--debug` / `-g` flag reference.
> - [Compiler Architecture](ARCHITECTURE.md) for the compilation pipeline.

---

## Table of Contents

1. [Quick Start](#quick-start)
2. [Prerequisites](#prerequisites)
3. [Compiling with Debug Info](#compiling-with-debug-info)
4. [VS Code Extension Setup](#vs-code-extension-setup)
5. [Launch Configuration Reference](#launch-configuration-reference)
6. [Choosing a Debugger](#choosing-a-debugger)
7. [Debugging Workflow](#debugging-workflow)
8. [Debugging with GDB / LLDB Directly](#debugging-with-gdb--lldb-directly)
9. [Troubleshooting](#troubleshooting)

---

## Quick Start

```bash
# 1. Compile with debug info
ashes compile --debug examples/hello.ash -o hello

# 2. Open VS Code, set breakpoints, then start the Ashes debug configuration
#    from Run and Debug
```

---

## Prerequisites

| Requirement | Minimum Version | Notes |
|-------------|----------------|-------|
| **Ashes compiler** | Latest | `ashes compile --debug` support |
| **GDB** *or* **LLDB** | GDB 10.0+ / LLDB 14.0+ | Native debugger backend |
| **VS Code** | 1.80+ | IDE with debugging UI |
| **Ashes VS Code extension** | 0.0.1+ | Language support, diagnostics, and debugging |

### Installing GDB

**Linux (Debian/Ubuntu):**

```bash
sudo apt install gdb
```

**Linux (Fedora/RHEL):**

```bash
sudo dnf install gdb
```

**macOS (via Homebrew):**

```bash
brew install gdb
```

> **Note:** On macOS, GDB requires code-signing. See
> [GDB on macOS](https://sourceware.org/gdb/wiki/PermissionsDarwin).
> Using LLDB is recommended on macOS instead.

**Windows (via MSYS2):**

```bash
pacman -S mingw-w64-x86_64-gdb
```

### Installing LLDB

The Ashes DAP server communicates with LLDB through `lldb-mi` (the LLDB
Machine Interface driver). Install LLDB **and** `lldb-mi`:

**Linux (Debian/Ubuntu):**

```bash
sudo apt install lldb lldb-mi
```

**macOS:**

LLDB is pre-installed with Xcode Command Line Tools. Install `lldb-mi`
separately if it is not already available:

```bash
brew install llvm          # includes lldb-mi
export PATH="$(brew --prefix llvm)/bin:$PATH"
```

> **Tip:** Set the `ashes.debugger` VS Code setting to `"lldb"` (see
> [Choosing a Debugger](#choosing-a-debugger) below).

### Installing the VS Code Extension

The Ashes VS Code extension bundles everything you need: language support
(syntax highlighting, diagnostics, formatting) **and** debug support (DAP
adapter, breakpoints, stepping).

**From marketplace (future):**

```bash
code --install-extension mattiashognas.ashes-vscode
```

**Local development build:**

```bash
cd vscode-extension
npm install
npm run build-lsp-server  # Build the LSP server
npm run build-dap-server  # Build the DAP server
npm run compile           # Build the extension
```

Then install via **Ctrl+Shift+P** → **Extensions: Install from VSIX…** or
use the local install script:

```powershell
.\scripts\install-vscode-extension-local.ps1
```

The extension automatically uses the bundled DAP server — no manual PATH
configuration is needed.

---

## Compiling with Debug Info

Use the `--debug` or `-g` flag with `ashes compile` or `ashes run`:

```bash
# Compile with debug info (defaults to -O0)
ashes compile --debug main.ash -o main

# Short form
ashes compile -g main.ash

# Compile with debug info and explicit optimization
ashes compile --debug -O1 main.ash

# Run with debug info
ashes run --debug main.ash
```

### What `--debug` Does

| Effect | Description |
|--------|-------------|
| **DWARF metadata** | Embeds `.debug_info`, `.debug_line`, `.debug_abbrev`, etc. into the binary. |
| **Optimization cap** | Without explicit `-O`, defaults to `-O0`. With explicit `-O`, caps at `-O1`. |
| **Source mapping** | Each IR instruction carries the source file, line, and column it originated from. |
| **Compilation output** | An extra `Debug: yes` line appears in the success summary. |

### Optimization Interaction

| Flags | Effective Optimization |
|-------|----------------------|
| `--debug` | `-O0` |
| `--debug -O0` | `-O0` |
| `--debug -O1` | `-O1` |
| `--debug -O2` | `-O1` (capped) |
| `--debug -O3` | `-O1` (capped) |

Higher optimization levels are capped at `-O1` when debugging is enabled
because aggressive optimization can eliminate variables, reorder code, and
inline functions — all of which make source-level debugging unreliable.

---

## VS Code Extension Setup

### Step 1: Install the Ashes Extension

Install the Ashes VS Code extension — it includes both language support and
debugging support. See [Prerequisites](#prerequisites) for installation
instructions.

### Step 2: Create a Launch Configuration

Create `.vscode/launch.json` in your project root:

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

> **Tip:** VS Code can auto-generate this. Click **Run > Add Configuration…**
> and select **Ashes: Launch** from the snippet list.

### Step 3: Create a Build Task (Optional)

Create `.vscode/tasks.json` to compile before debugging:

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "ashes: compile (debug)",
      "type": "shell",
      "command": "ashes",
      "args": [
        "compile",
        "--debug",
        "${file}",
        "-o",
        "${workspaceFolder}/out/${workspaceFolderBasename}"
      ],
      "group": {
        "kind": "build",
        "isDefault": true
      },
      "problemMatcher": []
    }
  ]
}
```

Then add `"preLaunchTask": "ashes: compile (debug)"` to your launch
configuration to auto-compile before each debug session.

### Step 4: Set Breakpoints and Debug

1. Open a `.ash` file in VS Code.
2. Click in the gutter (left of line numbers) to set breakpoints.
3. Press **F5** to start debugging.
4. Use the Debug toolbar to step through code.

---

## Launch Configuration Reference

All properties for `type: "ashes"` launch configurations:

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `program` | `string` | **Yes** | — | Path to the compiled Ashes binary. Must be compiled with `--debug`. Supports VS Code variables like `${workspaceFolder}`. |
| `args` | `string[]` | No | `[]` | Command-line arguments passed to the program at runtime. |
| `cwd` | `string` | No | `${workspaceFolder}` | Working directory for the debugged program. |
| `stopOnEntry` | `boolean` | No | `false` | When `true`, the debugger pauses at the program entry point before any user code runs. |
| `debuggerType` | `string` | No | *(from setting)* | Native debugger backend: `"gdb"` or `"lldb"`. When omitted, the value of the `ashes.debugger` extension setting is used. |
| `debuggerPath` | `string` | No | *(auto)* | Path to the debugger binary. Defaults to `gdb` when `debuggerType` is `"gdb"`, or `lldb-mi` when `"lldb"`. Set this if the binary is not on your `PATH`. |

### Example Configurations

**Minimal — single file:**

```json
{
  "name": "Debug hello.ash",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/hello"
}
```

**With arguments:**

```json
{
  "name": "Debug with args",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/out/myapp",
  "args": ["--input", "data.txt"],
  "cwd": "${workspaceFolder}"
}
```

**Custom GDB path:**

```json
{
  "name": "Debug (custom GDB)",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/out/myapp",
  "debuggerPath": "/usr/local/bin/gdb"
}
```

**Using LLDB (macOS):**

```json
{
  "name": "Debug (LLDB)",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/out/myapp",
  "debuggerType": "lldb"
}
```

**Stop on entry (useful for inspecting initial state):**

```json
{
  "name": "Debug (stop on entry)",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/out/myapp",
  "stopOnEntry": true
}
```

**Project with pre-launch build:**

```json
{
  "name": "Debug project",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/out/myproject",
  "preLaunchTask": "ashes: compile (debug)"
}
```

---

## Choosing a Debugger

The Ashes DAP server supports two native debugger backends:

| Backend | Binary | Best For | Notes |
|---------|--------|----------|-------|
| **GDB** | `gdb` | Linux, Windows (MSYS2) | Default. Mature MI support. |
| **LLDB** | `lldb-mi` | macOS, Linux | Uses LLDB-MI, the GDB-MI–compatible driver for LLDB. |

### Extension Setting

Set the default debugger for all launch configurations:

1. Open **Settings** (Ctrl+,).
2. Search for `ashes.debugger`.
3. Choose `gdb` or `lldb`.

Or in `settings.json`:

```json
{
  "ashes.debugger": "lldb"
}
```

### Per-Configuration Override

Add `"debuggerType"` to a specific launch configuration to override the
extension setting:

```json
{
  "name": "Debug with LLDB",
  "type": "ashes",
  "request": "launch",
  "program": "${workspaceFolder}/out/myapp",
  "debuggerType": "lldb"
}
```

---

## Debugging Workflow

### Setting Breakpoints

- **Line breakpoints:** Click in the gutter or press **F9** on a line.
- Breakpoints map to the `.ash` source file and line number via DWARF
  `.debug_line` information.

### Stepping

| Action | Keyboard | Description |
|--------|----------|-------------|
| Continue | **F5** | Resume execution until next breakpoint or exit. |
| Step Over | **F10** | Execute the current line and stop at the next line. |
| Step Into | **F11** | Step into a function call. |
| Step Out | **Shift+F11** | Execute until the current function returns. |
| Stop | **Shift+F5** | Terminate the debugging session. |

### Inspecting Variables

When paused at a breakpoint, the **Variables** pane in VS Code shows local
bindings and their values. Ashes types are displayed as:

| Ashes Type | Display Example |
|------------|----------------|
| `Int` | `42` |
| `Float` | `3.14` |
| `Bool` | `true` |
| `String` | `"hello"` |

> **Note:** Complex types (lists, ADTs, closures) display as raw memory values
> in the initial implementation. Pretty-printing is planned for a future phase.

---

## Debugging with GDB / LLDB Directly

You can also debug Ashes binaries directly without VS Code:

### GDB

```bash
# Compile with debug info
ashes compile --debug main.ash -o main

# Start GDB
gdb ./main

# In GDB:
(gdb) break main.ash:5        # Set breakpoint at line 5
(gdb) run                      # Start execution
(gdb) next                     # Step over
(gdb) step                     # Step into
(gdb) print x                  # Print variable
(gdb) backtrace                # Show call stack
(gdb) continue                 # Continue execution
(gdb) quit                     # Exit GDB
```

### LLDB

```bash
# Compile with debug info
ashes compile --debug main.ash -o main

# Start LLDB
lldb ./main

# In LLDB:
(lldb) breakpoint set --file main.ash --line 5
(lldb) run
(lldb) next
(lldb) step
(lldb) frame variable          # Show locals
(lldb) bt                      # Show call stack
(lldb) continue
(lldb) quit
```

---

## Troubleshooting

### "Cannot start debugging: no program specified"

Ensure your `launch.json` has a `program` property pointing to a compiled
binary (not a `.ash` source file). The binary must be compiled with `--debug`.

### "Failed to start GDB" / "Failed to start LLDB-MI"

- Verify the debugger is installed: `gdb --version` or `lldb-mi --version`
- If the binary is not on `PATH`, set `debuggerPath` in your launch configuration.
- On macOS, GDB requires code-signing — use LLDB instead by setting
  `"debuggerType": "lldb"` or `"ashes.debugger": "lldb"` in VS Code settings.

### Breakpoints not hitting

- Ensure the binary was compiled with `--debug` (or `-g`).
- Ensure optimization is at `-O0` or `-O1` (higher levels may optimize away
  breakpoint locations).
- Verify the source file path in the breakpoint matches the path used during
  compilation.

### "ashes-dap not found" or "DAP server not found"

The DAP server is bundled with the Ashes VS Code extension. If you see this
error, the extension was not built with the DAP server:

```bash
cd vscode-extension
npm run build-dap-server
```

If you installed from the marketplace, try reinstalling the extension.

### Variables show as `<optimized out>`

This happens when optimization eliminates variables. Recompile with `-O0`:

```bash
ashes compile --debug -O0 main.ash -o main
```

### Debug info not present in binary

Verify with `readelf`:

```bash
readelf -S ./main | grep debug
```

You should see sections like `.debug_info`, `.debug_line`, `.debug_abbrev`,
etc. If not, the binary was not compiled with `--debug`.

---

## Architecture

```
┌──────────┐     DAP/stdio      ┌───────────┐     GDB-MI      ┌──────────┐     ptrace     ┌────────┐
│  VS Code │ ◄─────────────────► │ ashes-dap │ ◄──────────────► │ GDB or   │ ◄────────────► │ Binary │
│  (IDE)   │                     │ (server)  │                  │ LLDB-MI  │                │ (DWARF)│
└──────────┘                     └───────────┘                  └──────────┘                └────────┘
```

1. **VS Code** sends DAP requests (setBreakpoints, continue, stackTrace, etc.)
   over stdin/stdout to the DAP server.
2. **ashes-dap** translates DAP requests into Machine Interface (MI) commands
   and sends them to the selected debugger (GDB or LLDB-MI).
3. **GDB** or **LLDB-MI** uses `ptrace` to control the debuggee and reads
   DWARF debug info from the binary to map machine code back to source
   locations.
4. Responses flow back through the same chain.
