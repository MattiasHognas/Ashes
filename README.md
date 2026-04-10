# Ashes

A small, **pure functional language** in the ML family — compiled directly to
native executables without runtime dependencies.

```ash
import Ashes.IO as io
import Ashes.List as list
import Ashes.Result as result
import Ashes.Async as task
type Shape =
    | Circle(Float)
    | Rect(Float, Float)

let area s = 
    match s with
        | Circle(r) -> 3.14159 * r * r
        | Rect(w, h) -> w * h
in 
    let shapes = [Circle(5.0), Rect(3.0)(4.0), Circle(1.0)]
    in 
        let t = 
            async
                let count = 
                    await task.fromResult(shapes
                    |> list.map(area)
                    |> list.map(fun (a) -> 
                        if a >= 10.0
                        then Ok(a)
                        else Error("too small"))
                    |> list.filter(result.isOk)
                    |> list.length
                    |> Ok)
                in count
        in 
            match task.run(t) with
                | Ok(n) when n >= 1 -> io.print(n)
                | Ok(_) -> io.print(0)
                | Error(_) -> io.print(0)
```

No GC. No runtime. Just a native binary.

---

## What is Ashes?

Ashes compiles `.ash` source files to **standalone native executables**
(ELF on Linux, PE on Windows) using a compiler written in .NET. The
produced binaries have **zero runtime dependencies** — no external
assembler, toolchain, or garbage collector required - and are optimized trough LLVM.

This repository contains the full toolchain:
- Compiler
- Formatter
- Test runner
- Language server
- Debugger
- Package manager
- VS Code extension

### Design Principles

| Principle | What it means |
|---|---|
| **Pure** | No mutation, no side effects in expressions |
| **Immutable** | All bindings are immutable, lists are linked lists |
| **Expression-based** | Everything evaluates to a value — no statements |
| **Strictly evaluated** | No lazy evaluation — arguments evaluated before calls |
| **Recursion-based** | Recursion and pattern matching replace loops |
| **Type-inferred** | Hindley-Milner type inference — types without annotations |

### Inspirations

- **ML / OCaml / F#** — algebraic data types, pattern matching, type inference
- **Elm** — simplicity and explicit data modeling
- **Haskell** — purity and type-driven design
- **Rust** — deterministic memory management without a GC

---

## A Tour of Ashes

### Bindings & Functions

```ash
let double = fun (x) -> x + x
in Ashes.IO.print(double(21))
```

### Pattern Matching & ADTs

```ash
type Color =
    | Red
    | Green
    | Blue

let name = fun (c) ->
    match c with
        | Red   -> "red"
        | Green -> "green"
        | Blue  -> "blue"

in Ashes.IO.print(name(Green))
```

### Lists & Recursion

```ash
let rec sum = fun (lst) -> fun (acc) ->
    match lst with
        | []        -> acc
        | x :: rest -> sum(rest)(acc + x)

in Ashes.IO.print(sum([1, 2, 3, 4, 5])(0))
```

### Pipelines

```ash
import Ashes.Result as result

"42"
|> parseOr
|> result.map(fun (n) -> n + 1)
|> result.default(0)
|> Ashes.IO.print
```

### Async/Await

```ash
import Ashes.Async as task

let work = async
    let! a = task.fromResult(21)
    in
        let! b = task.fromResult(21)
        in a + b
in task.run(work) |> Ashes.IO.print
```

### Polymorphism

Hindley-Milner let-polymorphism — use the same function at different types:

```ash
let id = fun (x) -> x
in
    let _ = id(42)
    in
        let _ = id("hello")
        in Ashes.IO.print("ok")
```

---

## Standard Library

| Module | Purpose |
|---|---|
| `Ashes.IO` | Console I/O, `print`, `panic`, `args`, line-based input |
| `Ashes.File` | UTF-8 file read/write/exists returning `Result` |
| `Ashes.Http` | HTTP/1.1 GET/POST for plain `http://` URLs |
| `Ashes.Net.Tcp` | Blocking TCP client (connect, send, receive, close) |
| `Ashes.Async` | `run`, `fromResult`, `sleep`, `all`, `race` |
| `Ashes.List` | `map`, `filter`, `fold`, `length`, `head`, `reverse`, ... |
| `Ashes.Maybe` | Helpers for the built-in `Maybe(T)` type |
| `Ashes.Result` | Helpers for the built-in `Result(E, A)` type |
| `Ashes.Test` | Assertion helpers for `.ash` tests |

Built-in types: `Int`, `Float`, `Bool`, `String`, `Unit`, `Maybe`, `Result`, `List`, `Socket`, `Task`

---

## Compiler Architecture

Ashes is split into focused phases:

| Project | Responsibility |
|---|---|
| **Ashes.Frontend** | Lexer, parser, AST |
| **Ashes.Semantics** | Binding, scope resolution, type inference |
| **Ashes.Backend** | IR lowering and native code generation |
| **Ashes.Formatter** | Canonical source formatting |
| **Ashes.Cli** | CLI orchestration (`compile`, `run`, `repl`, `test`, `fmt`, `init`, `add`, `remove`, `install`) |
| **Ashes.Lsp** | Language server (diagnostics, formatting, hover, completions) |
| **Ashes.Dap** | Debug server (gdb and lldb support) |
| **Ashes.TestRunner** | End-to-end `.ash` test execution |

### Compile Targets

| Target | Format | Architecture |
|---|---|---|
| `linux-x64` | ELF64 | x86-64 |
| `linux-arm64` | ELF64 | AArch64 |
| `windows-x64` | PE32+ | x86-64 |

```sh
dotnet run --project src/Ashes.Cli -- compile --target linux-x64 hello.ash -o hello
dotnet run --project src/Ashes.Cli -- compile --target linux-arm64 hello.ash -o hello
```

---

## Tooling

### CLI

```sh
ashes compile hello.ash              # compile to native binary
ashes run hello.ash                   # compile and run
ashes run -- arg1 arg2               # pass arguments
ashes repl                            # interactive REPL
ashes test tests                      # run end-to-end test suite
ashes fmt examples -w                 # auto-format in place
ashes init                            # create a new project
ashes add json-parser                 # add a dependency
ashes remove json-parser              # remove a dependency
ashes install                         # list project dependencies
```

### VS Code Extension

Full editor support with diagnostics, formatting, hover, go-to-definition,
semantic tokens, and completions.

For local development:

```powershell
.\scripts\install-vscode-extension-local.sh
```

### Debugging

Compile with `--debug` and use the Ashes VS Code extension (which bundles debug support):

```sh
ashes compile --debug examples/hello.ash -o hello
```

See [docs/DEBUGGING.md](docs/DEBUGGING.md) for the full debugging guide,
including VS Code extension setup, launch configuration, and GDB usage.

---

## Testing

End-to-end tests use `// expect:` directives:

```ash
// expect: 15
let rec sum = fun (lst) -> fun (acc) ->
    match lst with
        | []        -> acc
        | x :: rest -> sum(rest)(acc + x)
in Ashes.IO.print(sum([1, 2, 3, 4, 5])(0))
```

```sh
dotnet run --project src/Ashes.Cli -- test tests     # end-to-end suite
dotnet run --project src/Ashes.Tests -- --no-progress # compiler unit tests
```

See [docs/TESTING.md](docs/TESTING.md) for the full testing reference.

---

## Examples

Explore the [`examples/`](examples/) directory:

| Example | What it shows |
|---|---|
| [`hello.ash`](examples/hello.ash) | Minimal output |
| [`pipeline.ash`](examples/pipeline.ash) | Pipeline operator `\|>` |
| [`color_match.ash`](examples/color_match.ash) | ADTs and pattern matching |
| [`tail_recursion.ash`](examples/tail_recursion.ash) | Tail-call optimization |
| [`closures.ash`](examples/closures.ash) | Closures capturing bindings |
| [`result_flow.ash`](examples/result_flow.ash) | `Result` pipelines |
| [`stdlib_overview.ash`](examples/stdlib_overview.ash) | Standard library tour |
| [`polymorphism_basics.ash`](examples/polymorphism_basics.ash) | Let-polymorphism |
| [`io_echo_all.ash`](examples/io_echo_all.ash) | Recursive I/O until EOF |
| [`http_get.ash`](examples/http_get.ash) | HTTP GET request |

Multi-file project examples: [`project_imports/`](examples/project_imports/),
[`project_using_libs/`](examples/project_using_libs/),
[`list_pipeline/`](examples/list_pipeline/),
[`result_flow/`](examples/result_flow/)

---

## Documentation

| Document | Contents |
|---|---|
| [Development Guide](docs/DEVELOPMENT.md) | Building, testing, and developing locally |
| [Language Specification](docs/LANGUAGE_SPEC.md) | Authoritative syntax and semantics |
| [Project Specification](docs/PROJECT_SPEC.md) | Multi-file project format |
| [Compiler Architecture](docs/ARCHITECTURE.md) | Pipeline, backend, memory model, linking |
| [IR Reference](docs/IR_REFERENCE.md) | Intermediate representation instruction set |
| [CLI Specification](docs/COMPILER_CLI_SPEC.md) | All CLI commands and flags |
| [Debugging Guide](docs/DEBUGGING.md) | Debug extension setup and usage |
| [Formatter Specification](docs/FORMATTER_SPEC.md) | Canonical formatting rules |
| [Diagnostics Reference](docs/DIAGNOSTICS.md) | Error codes and messages |
| [Testing Reference](docs/TESTING.md) | Test directives and conventions |
| [Standard Library](docs/STANDARD_LIBRARY.md) | Module-by-module API reference |
| [Future Features](docs/future/FUTURE_FEATURES.md) | Planned future work |

---

## Why "Ashes"?

The name comes from my beloved dog. Her enthusiasm for trying anything new
mirrors the spirit of this project — an experiment, a learning journey, and
a personal exploration of functional language design.

---

## Contributing

```sh
dotnet build Ashes.slnx
dotnet run --project src/Ashes.Tests -- --no-progress
dotnet run --project src/Ashes.Cli -- test tests
```

Use the documents in [`docs/`](docs/) as the source of truth before changing
language or CLI behavior.

------------------------------------------------------------------------

## License

MIT — see [LICENSE](LICENSE).
