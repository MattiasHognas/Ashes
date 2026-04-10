# Ashes for Visual Studio Code

Full language support for the [Ashes](https://github.com/MattiasHognas/Ashes)
pure functional programming language.

![Ashes](icon.png)

## Features

- **Syntax highlighting** — TextMate grammar for `.ash` files
- **Diagnostics** — real-time error and warning squiggles from the Ashes language server
- **Formatting** — auto-format on save using the canonical Ashes formatter
- **Hover** — type information and documentation on hover
- **Go to Definition** — jump to function and binding definitions
- **Completions** — context-aware IntelliSense suggestions
- **Semantic tokens** — rich token-level highlighting
- **Debugging** — integrated DAP support with breakpoints, stepping, and variable inspection (GDB / LLDB backends)
- **Compile / Run / Test** — run Ashes programs directly from the editor

## Quick Start

1. Install the extension from the VS Code Marketplace (or build locally).
2. Open a folder containing `.ash` files.
3. The language server starts automatically when you open an Ashes file.
4. Use **Ctrl+F5** to run or **Shift+F5** to compile the active file.

### Install Toolchain

Run **Ashes: Install Toolchain** from the Command Palette (`Ctrl+Shift+P`) to
pre-download the compiler, language server, and debug adapter in one step.

## Commands

| Command | Default Key | Description |
|---|---|---|
| **Ashes: Compile** | `Shift+F5` | Compile the active `.ash` file or project |
| **Ashes: Run** | `Ctrl+F5` | Compile and run the active file/project |
| **Ashes: Run Tests** | — | Run tests in the workspace or active file |
| **Ashes: Install Toolchain** | — | Download compiler, LSP, and DAP binaries |

## Settings

| Setting | Default | Description |
|---|---|---|
| `ashes.autoStartLanguageServer` | `true` | Automatically start the language server when an `.ash` file is opened. Set to `false` to disable automatic startup. |
| `ashes.debugger` | `"gdb"` | Native debugger backend (`gdb` or `lldb`) |
| `ashes.lspServerPath` | `""` | Override the language server binary path |
| `ashes.dapServerPath` | `""` | Override the DAP server binary path |
| `ashes.compilerPath` | `""` | Override the compiler binary path |

## Debugging

1. Compile your program with `--debug`:
   ```sh
   ashes compile --debug myprogram.ash -o myprogram
   ```
2. Add a launch configuration (or let the extension generate one):
   ```jsonc
   {
     "name": "Ashes: Launch",
     "type": "ashes",
     "request": "launch",
     "program": "${workspaceFolder}/out/${workspaceFolderBasename}",
     "cwd": "${workspaceFolder}",
     "stopOnEntry": false
   }
   ```
3. Press **F5** to start debugging.

## A Taste of Ashes

```ash
import Ashes.IO as io
import Ashes.List as list

type Shape =
    | Circle(Float)
    | Rect(Float, Float)

let area = fun (s) ->
    match s with
        | Circle(r) -> 3.14159 * r * r
        | Rect(w, h) -> w * h

in
    [Circle(5.0), Rect(3.0)(4.0), Circle(1.0)]
    |> list.map(area)
    |> list.map(io.print)
```

## Workspace Trust

This extension requires a **trusted workspace** because it downloads and
executes external toolchain binaries. In an untrusted workspace, all
download-related features are disabled and an informational message is shown.

## Requirements

- VS Code 1.110+
- Node 20.19+ (extension host)
- Linux x64, Linux arm64, or Windows x64

## Links

- [Ashes repository](https://github.com/MattiasHognas/Ashes)
- [Language specification](https://github.com/MattiasHognas/Ashes/blob/main/docs/LANGUAGE_SPEC.md)
- [Debugging guide](https://github.com/MattiasHognas/Ashes/blob/main/docs/DEBUGGING.md)

## License

MIT — see [LICENSE](https://github.com/MattiasHognas/Ashes/blob/main/LICENSE).
