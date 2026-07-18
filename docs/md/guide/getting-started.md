# Getting Started

Ashes compiles `.ash` source files to standalone native executables — ELF on Linux, PE on
Windows — with zero runtime dependencies. This page takes you from nothing to a running
binary.

## Install

### Prebuilt binaries

Download the archive for your platform from the
[latest GitHub release](https://github.com/MattiasHognas/Ashes/releases/latest)
(`ashes-linux-x64.zip`, `ashes-linux-arm64.zip`, or `ashes-win-x64.zip`), unpack it, and put
the `ashes` executable on your `PATH`:

```sh
unzip ashes-linux-x64.zip -d ~/.local/share/ashes
ln -s ~/.local/share/ashes/ashes ~/.local/bin/ashes
ashes --help
```

### VS Code extension

Install the **Ashes Programming Language** extension from the VS Code Marketplace. It
provides syntax highlighting, the language server, the debugger, and an
**Ashes: Install Toolchain** command that downloads the compiler for you.

### Build from source

The compiler is written in C#/.NET. See [Development](development.md) for the full
setup; the short version:

```sh
git clone https://github.com/MattiasHognas/Ashes
cd Ashes
bash scripts/download-llvm-native.sh --all
dotnet build Ashes.slnx
dotnet run --project src/Ashes.Cli -- --help
```

## Hello, world

Create `hello.ash`:

```ash
Ashes.IO.print("Hello, world!")
```

Run it directly:

```sh
ashes run hello.ash
```

Or compile a native executable and run that:

```sh
ashes compile hello.ash -o hello
./hello
```

The produced binary is self-contained — copy it to another machine of the same platform
and it just runs.

## A first program

Ashes is pure and expression-based: no mutation, no statements, no null. Programs are
declarations followed by an expression. Iteration is recursion plus pattern matching:

```ash
import Ashes.IO as io
import Ashes.Collection.List as list

type Shape =
    | Circle(Float)
    | Rect(Float, Float)

let area shape =
    match shape with
        | Circle(r) -> 3.14159 * r * r
        | Rect(w, h) -> w * h

let shapes = [Circle(5.0), Rect(3.0)(4.0), Circle(1.0)]

shapes
    |> list.map(area)
    |> list.filter(given (a) -> a >= 10.0)
    |> list.length
    |> io.print
```

Read the [Language Reference](../reference/language.md) for the full syntax and
semantics, and the [Standard Library](../reference/standard-library.md) for the
module-by-module API.

## Everyday commands

One CLI does everything:

| Command | What it does |
|---|---|
| `ashes run file.ash` | Compile and immediately execute |
| `ashes compile file.ash -o out` | Produce a native executable |
| `ashes fmt file.ash -w` | Canonically format in place |
| `ashes test tests` | Run `.ash` tests with `// expect:` directives |
| `ashes repl` | Interactive REPL |
| `ashes init` | Scaffold a multi-file project (`ashes.json`) |

See the [CLI reference](../reference/cli.md) for every command and flag, cross-compilation
targets (`--target`), and optimization levels.

## Next steps

- [Projects](projects.md) — multi-file programs, `ashes.json`, imports and modules
- [Testing](testing.md) — the `.ash` test directive surface
- [Debugging](debugging.md) — compile with `--debug` and step through with gdb/lldb or VS Code
- [Compiler architecture](../internals/architecture.md) — how the pipeline works under the hood
