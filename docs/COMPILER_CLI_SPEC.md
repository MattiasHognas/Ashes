# Ashes Compiler CLI Specification

This document is the authoritative reference for every command, flag, and argument supported by the **Ashes compiler CLI** (`ashes`).

> **Source of truth:** The CLI entry point and all argument-parsing logic live in
> [`src/Ashes.Cli/Program.cs`](../src/Ashes.Cli/Program.cs).
> Update that file—and this document—together whenever CLI behaviour changes.

---

## Overview

The Ashes CLI is invoked as:

```
ashes <command> [options] [arguments]
```

Running `ashes` with no arguments (or an unrecognised command) prints the help text and exits with code **2**.

Running `ashes --help` (or `ashes -h`) prints the same help text and exits with code **0**.
Running `ashes <command> --help` (or `ashes <command> -h`) also prints the CLI help text and exits with code **0**.

---

## Command List

| Command           | Short description                                       |
|-------------------|---------------------------------------------------------|
| `ashes compile`   | Compile an Ashes source file/expression to a native executable |
| `ashes run`       | Compile and immediately execute an Ashes program        |
| `ashes repl`      | Start an interactive read-eval-print loop               |
| `ashes test`      | Run `.ash` test files and compare against `// expect:` comments |
| `ashes fmt`       | Format `.ash` source files                             |

---

## Common Options

The following help flags are accepted at the top level and for each command:

| Option | Value type | Default | Repeatable | Description |
|--------|-----------|---------|------------|-------------|
| `--help` / `-h` | — | — | No | Print CLI help text and exit successfully. |

The following options are accepted by **compile**, **run**, **repl**, and **test**:

| Option | Value type | Default | Repeatable | Description |
|--------|-----------|---------|------------|-------------|
| `--target <id>` | enum | OS-dependent (see below) | No | Select the code-generation back end. |
| `-O0`\|`-O1`\|`-O2`\|`-O3` | enum | `-O2` | No | Select LLVM optimization level. |

**Optimization level values:**

| Flag | Meaning |
|------|---------|
| `-O0` | No optimization |
| `-O1` | Basic optimizations |
| `-O2` | Standard optimizations (default) |
| `-O3` | Aggressive optimizations |

`--project` is accepted by **compile**, **run**, and **test** only (not by `ashes repl`):

| Option | Value type | Default | Repeatable | Description |
|--------|-----------|---------|------------|-------------|
| `--project <path>` | file path | — | No | Load a project manifest (`ashes.json`). Mutually exclusive with an inline file argument and `--expr`. |

**`--target` values:**

| Value | Platform |
|-------|----------|
| `linux-x64` | Linux x86-64 — emits a native ELF64 binary (default on Linux/macOS) |
| `windows-x64` | Windows x86-64 — emits a native PE32+ binary (default on Windows) |

Any other value is rejected with an error message and exit code **1**.

---

## Command Reference

### `ashes compile`

Compile an Ashes program to a native executable on disk.

#### Synopsis

```
ashes compile [--target <id>] [-O0|-O1|-O2|-O3] [-o <output>] <input.ash>
ashes compile [--target <id>] [-O0|-O1|-O2|-O3] [-o <output>] --expr "<source>"
ashes compile [--target <id>] [-O0|-O1|-O2|-O3] [-o <output>] --project <ashes.json>
ashes compile [--target <id>] [-O0|-O1|-O2|-O3] [-o <output>]          # discovers ashes.json upward
```

#### Arguments

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `<input.ash>` | file path | No† | Path to the `.ash` source file to compile. |

† Exactly one input source must be provided: a positional file, `--expr`, or a project (explicit or auto-discovered).

#### Options

| Option | Long form | Value type | Default | Repeatable | Description |
|--------|-----------|-----------|---------|------------|-------------|
| `-o` | `--out` | file path | Derived from input name (see below) | No | Path for the compiled output binary. |
| `--expr` | | string | — | No | Inline Ashes source to compile instead of reading a file. |
| `--target` | | enum | OS default | No | Target back end (`linux-x64` or `windows-x64`). |
| `--project` | | file path | — | No | Path to an `ashes.json` project file. |
| `-O0`\|`-O1`\|`-O2`\|`-O3` | | enum | `-O2` | No | Select LLVM optimization level. |

**Default output path rules (when `-o` is not given):**

- Single file `examples/hello.ash` → `examples/hello` (Linux) or `examples/hello.exe` (Windows).
- `--expr` without a project → `out` (Linux) or `out.exe` (Windows).
- Project compile → `<outDir>/<name>` (from `ashes.json`), appending `.exe` on Windows.

#### Defaults

| Property | Default value |
|----------|---------------|
| `--target` | `linux-x64` on Linux/macOS, `windows-x64` on Windows |
| `-o` / `--out` | Derived from input (see above) |
| `-O0`..`-O3` | `-O2` (standard optimizations) |

#### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Compilation succeeded; binary written to disk. |
| `1` | Compilation error (parse, type, or code-generation failure), or user/input error such as missing input, file not found, wrong extension, or I/O failure. |
| `2` | Usage error (bad flag or conflicting options). |

#### Output (stdout / stderr)

Successful status messages are written to **stdout** via Spectre.Console.
On success, a confirmation line is printed:

```
OK  Wrote <size> to <output>
     Target: <target>
     Time:   <elapsed>
```

`<size>` is human-readable (e.g. `8.1 KB`).
`<elapsed>` is the compilation wall-clock time (e.g. `159ms` or `1.23s`).

Compiler diagnostics and command errors are written to **stderr**.
No machine-readable output format is currently supported.

#### Examples

```bash
# Compile a file (output: examples/hello on Linux)
ashes compile examples/hello.ash

# Compile to an explicit path
ashes compile examples/hello.ash -o build/hello

# Compile an inline expression
ashes compile --expr 'Ashes.IO.print(40 + 2)' -o out

# Cross-compile to Windows PE
ashes compile examples/hello.ash --target windows-x64 -o hello.exe

# Compile the project rooted at ashes.json
ashes compile --project path/to/ashes.json

# Auto-discover ashes.json (searches upward from cwd)
ashes compile
```

---

### `ashes run`

Compile and immediately execute an Ashes program. The compiled binary is written to a uniquely named executable under the system temporary directory (for example, `<temp>/ashes/`) and is not automatically deleted by the CLI.

#### Synopsis

```
ashes run [--target <id>] [-O0|-O1|-O2|-O3] <input.ash> [-- <args...>]
ashes run [--target <id>] [-O0|-O1|-O2|-O3] --expr "<source>" [-- <args...>]
ashes run [--target <id>] [-O0|-O1|-O2|-O3] --project <ashes.json> [-- <args...>]
ashes run [--target <id>] [-O0|-O1|-O2|-O3] [-- <args...>]   # auto-discovers ashes.json
```

#### Arguments

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `<input.ash>` | file path | No† | Path to the `.ash` source file to run. |
| `-- <args...>` | string list | No | Arguments forwarded to the compiled program (`args` in Ashes source). The `--` separator is required. |

† Same rule as `compile`: one of file, `--expr`, or project.

#### Options

| Option | Value type | Default | Repeatable | Description |
|--------|-----------|---------|------------|-------------|
| `--expr` | string | — | No | Inline Ashes source to compile and run. |
| `--target` | enum | OS default | No | Target back end. |
| `--project` | file path | — | No | Path to an `ashes.json` project file. |
| `-O0`\|`-O1`\|`-O2`\|`-O3` | enum | `-O2` | No | Select LLVM optimization level. |

#### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Program compiled and exited with code 0. |
| Non-zero (from program) | The compiled program's own exit code is propagated as-is. |
| `1` | Compilation error or user/input failure (before the program starts). |
| `2` | Usage error. |

#### Output (stdout / stderr)

The compiled program's **stdout** and **stderr** flow directly to the terminal without interception.
No extra diagnostic lines (timing, size, etc.) are printed, so `ashes run` output is safe to pipe.
Compiler diagnostics are printed to the CLI's **stderr** before the program runs.

#### Examples

```bash
# Run a file
ashes run examples/hello.ash

# Run with arguments passed to the program
ashes run examples/hello.ash -- hello world

# Run an inline expression
ashes run --expr 'Ashes.IO.print(40 + 2)'

# Run the auto-discovered project
ashes run

# Run a named project
ashes run --project examples/project/ashes.json
```

---

### `ashes repl`

Start an interactive read-eval-print loop. Each expression is compiled to a temporary binary, executed, and its stdout is displayed.

#### Synopsis

```
ashes repl [--target <id>] [-O0|-O1|-O2|-O3]
```

#### Options

| Option | Value type | Default | Repeatable | Description |
|--------|-----------|---------|------------|-------------|
| `--target` | enum | OS default | No | Target back end used for all REPL evaluations. |
| `-O0`\|`-O1`\|`-O2`\|`-O3` | enum | `-O2` | No | Select LLVM optimization level used for all REPL evaluations. |

#### REPL Commands (typed at the prompt)

| Command | Aliases | Description |
|---------|---------|-------------|
| `:help` | `:h` | Show REPL help. |
| `:quit` | `:q`, `:exit` | Exit the REPL (exit code 0). |
| `:target` | | Show the current target. |
| `:target linux-x64\|windows-x64` | | Change the active target for subsequent expressions. |

Multi-line input is supported: if the parser detects an incomplete expression (unbalanced parentheses or an expected-token error), the REPL shows a `...>` continuation prompt.

#### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | User exited cleanly (`:quit` / `:exit` / `:q`). |
| `2` | Usage error (unknown flag). |

#### Output (stdout / stderr)

All REPL output (prompts, results, errors) is written to **stdout**.
Compiled-program stderr is relayed and highlighted in red.

#### Examples

```bash
ashes repl
# ashes> Ashes.IO.print(40 + 2)
# 42
# ashes> :target linux-x64
# Target set to linux-x64
# ashes> :quit
```

---

### `ashes test`

Discover and execute `.ash` test files. A test file must contain a leading `// expect:` comment; the runner compiles the file, executes it, and compares stdout against the expected value.

#### Synopsis

```
ashes test [--target <id>] [-O0|-O1|-O2|-O3] [--project <ashes.json>] [paths...]
```

#### Arguments

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `[paths...]` | file/directory path list | No | One or more `.ash` files or directories to search for tests. Repeatable. When `--project` is supplied and no paths are given, the default search root is `<projectDir>/tests` if that directory exists, otherwise it is the project directory itself. |

#### Options

| Option | Value type | Default | Repeatable | Description |
|--------|-----------|---------|------------|-------------|
| `--target` | enum | OS default | No | Target back end for test compilation. |
| `--project` | file path | — | No | Load a project manifest. When no explicit `[paths...]` are given, test discovery uses `<projectDir>/tests` if that directory exists; otherwise it falls back to the project directory itself. |
| `-O0`\|`-O1`\|`-O2`\|`-O3` | enum | `-O2` | No | Select LLVM optimization level for test compilation. |

#### Test File Conventions

A test file is a regular `.ash` source file with one or more leading comment directives:

```
// expect: <expected stdout>
// exit: <expected exit code>   (optional; defaults to 0)
```

- Only the **first** `// expect:` line is used.
- `// exit:` must appear before `// expect:`.
- Files without `// expect:` are reported as **SKIP** (treated as pass).

Special expected value:

| Value | Meaning |
|-------|---------|
| `empty` | Expect empty stdout. |

Example:

```ash
// exit: 1
// expect: empty
panic("empty")
```

#### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | All tests with `// expect:` passed (or none found). |
| `1` | One or more tests failed. |
| `2` | Usage error. |

#### Output (stdout / stderr)

Results are printed as a table to **stdout**:

```
 Test          Result   Time
──────────────────────────────
 hello.ash     PASS     5ms
 failing.ash   FAIL     12ms
```

A summary line follows: `N passed, N failed, N skipped in <elapsed>`.
Failure details (expected vs. actual stdout) are printed after the table.

#### Examples

```bash
# Run all tests in the default location
ashes test

# Run tests in a specific directory
ashes test tests/

# Run a single test file
ashes test tests/hello.ash

# Run tests for a named project
ashes test --project path/to/ashes.json
```

---

### `ashes fmt`

Format `.ash` source files. `ashes fmt` resolves formatting from the nearest `.editorconfig` (walking upward from each file), supporting:

- `indent_style = space|tab`
- `indent_size = <int>|tab`
- `tab_width = <int>`
- `end_of_line = lf|crlf`

Defaults when not provided are 4 spaces and platform newline. Without `-w`, formatted output is printed to stdout. With `-w`, files are updated in place.

#### Synopsis

```
ashes fmt <file|dir> [-w]
```

#### Arguments

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `<file\|dir>` | file/directory path | Yes | A single `.ash` file or a directory. When a directory is given, all `.ash` files are found recursively. |

#### Options

| Option | Long form | Value type | Default | Repeatable | Description |
|--------|-----------|-----------|---------|------------|-------------|
| `-w` | `--write` | bool (flag) | false | No | Write formatted output back to the source file(s) instead of printing to stdout. |

#### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Formatting succeeded (or no `.ash` files found). |
| `1` | Parse error in one of the source files, or user/input error such as missing path, wrong extension, or path not found. |
| `2` | Usage error (bad flag or ambiguous arguments). |

#### Output (stdout / stderr)

- Without `-w`: formatted source is written to **stdout**. When multiple files are formatted, a separator rule is printed before each file.
- With `-w`: a summary line (`OK  Formatted N file(s) in <elapsed>.`) is written to **stdout**. Files that are already correctly formatted are not rewritten.

#### Examples

```bash
# Preview formatting of a single file
ashes fmt examples/hello.ash

# Format all .ash files in a directory, writing in place
ashes fmt examples -w

# Preview formatting for all .ash files in a directory
ashes fmt examples
```

---

## Project File (`ashes.json`)

The project file enables multi-module compilation and controls build settings.

### Fields

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `entry` | string (file path) | **Yes** | — | Relative path to the entry-point `.ash` file. |
| `name` | string | No | Filename stem of `entry` | Output binary name (without extension). |
| `sourceRoots` | string array | No | `["."]` | Directories searched when resolving `import` statements. |
| `include` | string array | No | `[]` | Additional directories searched for imported modules. |
| `outDir` | string | No | `"out"` | Directory where the compiled binary is written. |
| `target` | string (enum) | No | OS default | Default back end target; overridden by `--target` on the command line. |

### Example

```json
{
  "name": "myapp",
  "entry": "src/Main.ash",
  "sourceRoots": ["src"],
  "outDir": "build",
  "target": "linux-x64"
}
```

### Module Import Syntax

Within any `.ash` file in a project, modules can be imported with:

```ash
import ModuleName
import Namespace.ModuleName
```

- Module names must start with an uppercase letter.
- Nested namespaces use dot notation; each component must start with an uppercase letter.
- Import statements are collected from anywhere in the file; idiomatic style is to place them before any expression code.
- Circular imports are detected and reported as an error.

---

## Exit Code Summary

| Code | Meaning |
|------|---------|
| `0` | Success. |
| `1` | Compilation error, runtime error, test failure, or concrete user/input error. |
| `2` | Usage / argument error (bad flag, conflicting options, or unknown command syntax). |

The exit code from `ashes run` is the compiled program's own exit code when compilation succeeds.

---

## Validation Rules and Error Behaviour

| Condition | Error message / behaviour | Exit code |
|-----------|--------------------------|-----------|
| Unknown command | Help text printed | `2` |
| Unknown flag | `Unknown argument.` | `2` |
| Missing required compile/run input | `Missing input file or --expr.` | `1` |
| Missing required fmt path | `Missing file or directory.` | `1` |
| Input file not found | `File not found: <path>` | `1` |
| Input file has wrong extension | `Input file must have .ash extension (or use --expr).` | `1` |
| `--project` combined with file/`--expr` | `Cannot combine --project with input file or --expr.` | `2` |
| `--target` receives unknown value | Message indicating unknown target, e.g. `Unknown target '<value>'.` | `1` |
| `ashes.json` missing `entry` field | `Project file is missing required string field 'entry'.` | `1` |
| `ashes.json` entry file not found | `Project entry file not found: <path>` | `1` |
| Import cycle detected | `Import cycle detected: A -> B -> A` | `1` |
| Parse / type error in source | Diagnostic message | `1` |
| `ashes fmt` given more or fewer than one path | `Provide exactly one file or directory.` | `2` |
| `ashes fmt` path does not exist | `Path not found: <path>` | `1` |

---

## Compatibility Rules

### Breaking Changes

The following changes would break existing users or tooling and must be treated as breaking:

- Removing a command or flag.
- Changing the meaning of an existing flag.
- Changing exit codes for existing conditions.
- Changing the format of the success output line for `ashes compile`.

### Non-Breaking Changes

The following changes are considered non-breaking:

- Adding new commands or flags.
- Adding new `--target` values.
- Adding new fields to `ashes.json` (with defaults).
- Changing diagnostic or error message wording (not the exit code).
- Changing the visual styling of terminal output (colours, table borders).
