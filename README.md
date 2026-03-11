# Ashes

Ashes is a small experimental **pure functional programming language**
compiled by a compiler written in .NET directly to native executables
(ELF on Linux and PE on Windows), without requiring an external
assembler or toolchain.

Ashes is both a learning project and an exploration of what a minimal
modern functional language can look like.

------------------------------------------------------------------------

## Language Philosophy

Ashes belongs to the **ML family** of functional languages.

It is inspired by:

-   **ML / OCaml / F#** --- algebraic data types, pattern matching, and
    type inference
-   **Elm** --- simplicity and explicit data modeling with algebraic data types
-   **Haskell** --- purity and type-driven design
-   **Rust** --- strict memory safety without the need of a GC

Ashes intentionally keeps a small core:

-   everything is an expression
-   immutable bindings only
-   strict evaluation
-   recursion instead of loops
-   explicit data modeling via pattern matching
-   clarity over feature count

------------------------------------------------------------------------

## Why?

The name *Ashes* comes from my beloved dog. Her enthusiasm for trying
anything new mirrors the spirit of this project.

This repository is:

-   an experiment
-   a learning journey
-   and a personal exploration of functional language design.

------------------------------------------------------------------------

## Repository Structure

Ashes is split into independent components:

-   **Frontend** --- lexer, parser, AST
-   **Semantics** --- binding and type inference
-   **Backend** --- native code generation
-   **Formatter** --- canonical source formatting
-   **CLI** --- compiler interface
-   **LSP** --- language server
-   **TestRunner** --- `.ash` program testing

------------------------------------------------------------------------

## Specifications

Authoritative specifications:

- [Language Specification](docs/LANGUAGE_SPEC.md)
- [Project Specification](docs/PROJECT_SPEC.md)
- [Compiler CLI Specification](docs/COMPILER_CLI_SPEC.md)
- [Formatter Specification](docs/FORMATTER_SPEC.md)
- [Diagnostics Reference](docs/DIAGNOSTICS.md)
- [Testing Reference](docs/TESTING.md)
- [Standard Library Reference](docs/STANDARD_LIBRARY.md)

The README provides only a high-level overview.

Standard library functions live under `Ashes.*` modules. The core IO surface lives under
`Ashes.IO`:

-   `Ashes.IO.print(expr)` — prints to standard output
-   `Ashes.IO.panic("message")` — aborts execution
-   `Ashes.IO.args` — `List<String>` of command-line arguments
-   `Ashes.File.readText(path)` — `Result<String, String>` UTF-8 file read
-   `Ashes.File.writeText(path, text)` — `Result<String, Unit>` UTF-8 file write
-   `Ashes.File.exists(path)` — `Result<String, Bool>` existence check
-   `Ashes.Net.Tcp.connect(host)(port)` — `Result<String, Socket>` TCP connect
-   `Ashes.Net.Tcp.send(socket)(text)` — `Result<String, Int>` TCP send
-   `Ashes.Net.Tcp.receive(socket)(maxBytes)` — `Result<String, String>` TCP receive
-   `Ashes.Net.Tcp.close(socket)` — `Result<String, Unit>` TCP close

`Ashes` is reserved for compiler-provided standard library modules and cannot be defined by user projects.

When importing multi-segment modules, full qualification such as `Foo.Bar.value` always works.
Short qualification such as `Bar.value` also works when `Bar` is the unique imported leaf module
qualifier. If imported exports collide, unqualified access fails; if imported leaf qualifiers
collide, short qualification fails and full qualification must be used.

Pure shipped helper libraries live under the compiler `lib/` folder. Reserved
`Ashes.*` modules are compiler-provided and are not overridable by project-local
modules.

Shipped source-backed standard-library helper modules currently include:

- `Ashes.List`
- `Ashes.Maybe`
- `Ashes.Result`
- `Ashes.Test`

Example:

    import Ashes.List
    import Ashes.IO
    print(Ashes.List.length([1, 2, 3]))

------------------------------------------------------------------------

## Examples

Start with these runnable examples:

- `examples/hello.ash` --- minimal output
- `examples/args_demo.ash` --- command-line argument access via `Ashes.IO.args`
- `examples/io_hello.ash` --- minimal explicit IO output via `Ashes.IO.writeLine`
- `examples/io_prompt.ash` --- prompt and response using `Ashes.IO.readLine()`
- `examples/io_echo_once.ash` --- single-line pipe-friendly echo
- `examples/io_echo_all.ash` --- recursive echo until EOF
- `examples/float_ops.ash` --- float arithmetic and comparison smoke test
- `examples/float_backend_demo.ash` --- backend float arithmetic/comparison smoke test
- `examples/fs_read_text.ash` --- read UTF-8 text from a file
- `examples/fs_write_text.ash` --- write and reread UTF-8 text
- `examples/fs_exists.ash` --- filesystem existence check
- `examples/result_flow.ash` --- standalone `Result` workflow using `Ashes.Result`
- `examples/tcp_connect.ash` --- TCP connect with Result handling
- `examples/tcp_send.ash` --- TCP send with Result handling
- `examples/tcp_receive.ash` --- TCP receive with Result handling
- `examples/tcp_close.ash` --- TCP close with Result handling

Project-mode shipped-library examples:

- `examples/pipeline_list_map/`
- `examples/list_filter_fold/`
- `examples/option_default/`
- `examples/result_flow/`
- `examples/args_demo.ash` --- command-line argument access via `Ashes.IO.args`

Project-style examples also live under:

- `examples/project_imports/`
- `examples/project_using_libs/`
- `examples/list_pipeline/`
- `examples/list_fold/`
- `examples/option_flow/`
- `examples/result_flow/`

------------------------------------------------------------------------

## Example

    import Ashes.IO
    let rec sum =
        fun (lst) ->
            fun (acc) ->
                match lst with
                    | [] -> acc
                    | x :: rest -> sum(rest)(acc + x)
    in print(sum([1, 2, 3])(1))

Output:

    7

------------------------------------------------------------------------

## Supported Language Features

### Core

-   integers

-   booleans (`true`, `false`)

-   strings with escapes

-   `if / then / else`

-   immutable bindings

        let x = expr in expr
        let rec f = fun (...) -> ... in expr

-   lambdas: `fun (x, y) -> expr`

-   function application: `f(x)` or `f x` (ML-style whitespace application)

-   `+` for integers and strings

-   `-`, `*`, `/` for integer arithmetic

-   unary `-` negation for integers (for example `-x`, `-1`)

-   `>=` and `<=` for integer comparison

-   `==` and `!=` for integer/string equality comparison

-   tuples: `(a, b, ...)` with tuple patterns in `match`

-   standard library: `Ashes.IO` module (`print`, `panic`, `args`)

-   let-polymorphism (Hindley-Milner style) for non-recursive `let` bindings

### Polymorphism

Ashes generalizes non-recursive `let` bindings, so polymorphic helpers can be reused at
multiple concrete types. `let rec` remains monomorphic during inference.

    let id = fun (x) -> x
        in
        let _a = id(1)
            in
            let _b = id("x")
                in Ashes.IO.print("ok")

### Lists

-   empty list: `[]`

-   literals: `[1,2,3]`

-   cons operator: `x :: xs`

-   pattern matching

        match xs with
            | [] -> ...
            | x :: rest -> ...

-   wildcard: `_` matches any value and binds nothing

### Algebraic Data Types

-   type declarations

        type Option =
            | None
            | Some(T)

-   constructor expressions: `Some(10)`, `None`

-   constructor patterns in `match`

        match opt with
            | None -> def
            | Some(x) -> x

------------------------------------------------------------------------

## Build & Run

### Build compiler and tests

    dotnet build Ashes.slnx
    dotnet run --project src/Ashes.Tests/Ashes.Tests.csproj

### Compile to native executable

    dotnet run --project src/Ashes.Cli -- compile \
      --expr "Ashes.IO.print(\"hello \" + \"world\")" -o out

    chmod +x out
    ./out

Output:

    hello world

------------------------------------------------------------------------

## Targets

Frontend stages are shared; backends are selectable:

-   `linux-x64` ELF64
-   `windows-x64` PE32+

Example:

    ashes compile --target linux-x64 --expr "Ashes.IO.print(42)" -o out

------------------------------------------------------------------------

## CLI

Compile file:

    ashes compile hello.ash

Compile expression:

    ashes compile --expr "Ashes.IO.print(40 + 2)" -o out

Compile project:

    ashes compile
    ashes compile --project path/to/ashes.json

------------------------------------------------------------------------

## Run Programs

    ashes run examples/hello.ash
    ashes run -- hello world

------------------------------------------------------------------------

## REPL

    ashes repl

------------------------------------------------------------------------

## Testing

`ashes test` runs `.ash` programs and compares stdout against:

    // expect:
    42

Ashes source supports `// ...` line comments (including these directives), and
comments are ignored by compilation.

Runtime failure tests:

    // exit: 1
    // expect: oh no!
    panic("oh no!")

Tests are discovered recursively under `tests/`, execute in stable
lexicographic order, and become project-aware automatically when an
`ashes.json` project file is discovered.

Supported directives are documented in [docs/TESTING.md](docs/TESTING.md).

If a fixture is intentionally parser-invalid and should be skipped by CI `fmt`
verification, annotate it with:

    // fmt-skip: <reason>

CI includes fast and full pipelines with coverage reporting.

------------------------------------------------------------------------

## Formatting

Canonical formatting (4-space indentation):

    ashes fmt examples/hello.ash
    ashes fmt examples -w

------------------------------------------------------------------------

## VS Code Extension

-   Extension sources: `vscode-extension/`
-   Language server: `src/Ashes.Lsp/`

Build server executables:

    cd vscode-extension
    npm run build-server

Install extension:

    code --install-extension ashes-vscode.vsix

### VS Code Extension Development

Install a local VSIX from the current Debug build without downloading compiler
artifacts from GitHub Releases:

    .\scripts\install-vscode-extension-local.ps1

..or if you want to target VS Code Insiders:

    .\scripts\install-vscode-extension-local.ps1 -CodeCommand code-insiders.cmd
------------------------------------------------------------------------

## License

MIT License --- see LICENSE.
