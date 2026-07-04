# Ashes

A small, **pure functional language** in the ML family - compiled directly to
native executables without runtime dependencies.

```ash
import Ashes.IO as io
import Ashes.List as list

type Shape =
    | Circle(Float)
    | Rect(Float, Float)

let area shape
    match shape with
        | Circle(r) -> 3.14159 * r * r
        | Rect(w, h) -> w * h
in 
    let shapes = [Circle(5.0), Rect(3.0)(4.0), Circle(1.0)]
    in 
        let work = 
            async(
                shapes
                |> list.map area
                |> list.filter(given(a) -> a >= 10.0)
                |> list.length
            )
        in 
            match await work with
                | Ok(count) -> io.print count
                | Error(_) -> io.print 0
```

No GC. No runtime. Just a native binary.

---

## What is Ashes?

Ashes compiles `.ash` source files to **standalone native executables**
(ELF on Linux, PE on Windows) using a compiler written in .NET. The
produced binaries have **zero runtime dependencies** - no external
assembler, toolchain, or garbage collector required - and are optimized trough LLVM.

This repository contains the full toolchain:
- Compiler
- Formatter
- Test runner
- Language server
- Debugger
- VS Code extension
- Package manager

### Design Principles

| Principle | What it means |
|---|---|
| **Pure** | No mutation, no side effects in expressions |
| **Immutable** | All bindings are immutable, lists are linked lists |
| **Expression-based** | Everything evaluates to a value - no statements |
| **Strictly evaluated** | No lazy evaluation - arguments evaluated before calls |
| **Recursion-based** | Recursion and pattern matching replace loops |
| **Type-inferred** | Hindley-Milner type inference - types without annotations |

### Inspirations

- **ML / OCaml / F#** - algebraic data types, pattern matching, type inference
- **Elm** - simplicity and explicit data modeling
- **Haskell** - purity and type-driven design
- **Rust** - deterministic memory management without a GC

---

## A Tour of Ashes

### Bindings & Functions

```ash
import Ashes.IO as io

let double x = x + x
in io.print(double(21))
```

### Pattern Matching & ADTs

```ash
import Ashes.IO as io

type Color =
    | Red
    | Green
    | Blue

let name c =
    match c with
        | Red   -> "red"
        | Green -> "green"
        | Blue  -> "blue"

in io.print(name(Green))
```

### Lists & Recursion

```ash
import Ashes.IO as io

let recursive sum lst acc =
    match lst with
        | []        -> acc
        | x :: rest -> sum(rest)(acc + x)

in io.print(sum([1, 2, 3, 4, 5])(0))
```

### Pipelines

```ash
import Ashes.Result as result
import Ashes.Text as text
import Ashes.IO as io

"42"
|> text.parseInt
|> result.map(given (n) -> n + 1)
|> result.default(0)
|> io.print
```

### Async/Await

```ash
import Ashes.Async as task
import Ashes.IO as io

let work = 
    async(match await task.all([async 21, async 21]) with
        | Error(_) -> 0
        | Ok(values) -> 
            match values with
                | a :: b :: [] -> a + b
                | _ -> 0)
in
    io.print(match await work with
        | Ok(n) -> n
        | Error(_) -> 0)
```

### Capabilities & Handlers

Business code declares the operations it needs; the caller decides what they
mean by installing a handler — dependency injection with no framework, checked
at compile time (an unsatisfied capability is a compile error, not a runtime crash):

```ash
capability Clock =
    | now : Unit -> Int

capability Log =
    | log : Str -> Unit

let stamped msg = 
    let _ = Log.log(msg)
    in Clock.now(Unit)

let result = 
    handle stamped("checkout") with
        | Clock.now(_) -> resume(1720000000)
        | Log.log(m) -> 
            let _ = Ashes.IO.writeLine("[log] " + m)
            in resume(Unit)
        | return(r) -> r

Ashes.IO.print(result)
```

Swap the handler and the same `stamped` runs against a real clock in
production or a frozen one in tests. Capability rows are inferred and appear in
types as `needs {Clock, Log}`; see
[LANGUAGE_SPEC.md](docs/LANGUAGE_SPEC.md) section 20.

### Polymorphism

Hindley-Milner let-polymorphism - use the same function at different types:

```ash
import Ashes.IO as io

let id x = x
in
    let _ = id(42)
    in
        let _ = id("hello")
        in io.print("ok")
```

---

## Standard Library

| Module | Purpose |
|---|---|
| `Ashes.IO` | Console I/O, `print`, `panic`, `args`, line-based input |
| `Ashes.File` | UTF-8 file read/write/exists returning `Result` |
| `Ashes.Http` | HTTP/1.1 GET/POST for `http://` and `https://` URLs |
| `Ashes.Net.Tcp` | Async TCP client (connect, send, receive, close) |
| `Ashes.Net.Tls` | Async TLS client (connect, send, receive, close) |
| `Ashes.Async` | `run`, `fromResult`, `sleep`, `all`, `race` |
| `Ashes.List` | `map`, `filter`, `fold`, `length`, `head`, `reverse`, ... |
| `Ashes.Maybe` | Helpers for the built-in `Maybe(T)` type |
| `Ashes.Result` | Helpers for the built-in `Result(E, A)` type |
| `Ashes.Text` | Unicode-aware `uncons` plus `parseInt` and `parseFloat` |
| `Ashes.Test` | Assertion helpers for `.ash` tests |

Built-in types: `Int`, `Float`, `Bool`, `Str`, `Unit`, `Maybe`, `Result`, `List`, `Socket`, `Task`, `TlsSocket`

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
| `win-x64` | PE32+ | x86-64 |

```sh
dotnet run --project src/Ashes.Cli -- compile --target linux-x64 hello.ash -o hello
dotnet run --project src/Ashes.Cli -- compile --target linux-arm64 hello.ash -o hello
```

---

## Tooling

### CLI

```sh
ashes compile hello.ash     # compile to native binary
ashes run hello.ash         # compile and run
ashes run -- arg1 arg2      # pass arguments
ashes repl                  # interactive REPL
ashes test tests            # run end-to-end test suite
ashes fmt examples -w       # auto-format in place
ashes init                  # create a new project
ashes add json-parser       # add a dependency
ashes remove json-parser    # remove a dependency
ashes install               # list project dependencies
```

### VS Code Extension

Full editor support with diagnostics, formatting, hover, go-to-definition,
semantic tokens, and completions.

For local development:

```sh
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
let recursive sum lst acc =
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

## Local CI/CD

The full CI/CD pipeline runs locally in rootless **Podman** containers — no
GitHub required. Jobs are driven by a `justfile` and reproduce the
`.github/workflows/{pull-request,push-to-main,release}` steps, with each
architecture in its own image: **linux-x64** natively, **linux-arm64** under
`qemu`, **win-x64** under `wine`.

```sh
./scripts/init-local-ci.sh   # one-command bootstrap (deps + images + LLVM libs)
just ci-quick                # fast inner loop: build + tests
just ci                      # full PR-equivalent pipeline
just release-github 1.2.3    # branch, build, tag, and publish a GitHub Release
```

`just install-hooks` wires `pre-commit` → `just ci-quick` and `pre-push` →
`just ci`. See [docs/LOCAL_CI.md](docs/LOCAL_CI.md) for the full guide
(recipes, releases, dependency/SAST scanning, and troubleshooting).

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
| [`text_parsing_demo.ash`](examples/text_parsing_demo.ash) | `Ashes.Text` parsing primitives |
| [`polymorphism_basics.ash`](examples/polymorphism_basics.ash) | Let-polymorphism |
| [`io_echo_all.ash`](examples/io_echo_all.ash) | Recursive I/O until EOF |
| [`http_get.ash`](examples/http_get.ash) | HTTP GET request |

Multi-file project examples: [`project_imports/`](examples/project_imports/),
[`project_using_libs/`](examples/project_using_libs/),
[`list_pipeline/`](examples/list_pipeline/),
[`result_flow/`](examples/result_flow/)

---

## Common Questions

**How is memory managed without a garbage collector?**
Heap values live on a per-thread bump arena and are reclaimed in bulk at
ownership-scope boundaries — the compiler tracks every owned value and inserts
deterministic cleanup; there is no GC, no reference counting, and nothing for
the user to annotate. Resource types (sockets) additionally get compile-time
use-after-close and double-close checking.
*Details: [Ownership Model](docs/LANGUAGE_SPEC.md#17-ownership-model) and
[Resource Types](docs/LANGUAGE_SPEC.md#16-resource-types-and-deterministic-cleanup)
in the spec; [Memory Model](docs/ARCHITECTURE.md#memory-model) in the
architecture guide.*

**Does Ashes require a runtime?**
No. `.ash` source compiles directly to a standalone native executable.There
is no VM, garbage collector, interpreter, runtime library, or hidden scheduler
that ships alongside the binary.
Ashes lowers to LLVM IR and uses LLVM for optimization and native object generation,
then performs the final object-to-executable linking itself using its own built-in
linker—without invoking external tools such as clang, ld, lld, or link.exe.
The result is a single native binary with no runtime dependencies.
*Details: [Compiler Architecture](docs/ARCHITECTURE.md#memory-model) and
[Linking](docs/ARCHITECTURE.md#linking).*

**How can immutable data be efficient?**
Ashes does not copy a value on every "update". Immutable values are freely
shared (never defensively copied), so `x` and a derived `y` transparently share
their common structure. On top of that the compiler rebuilds recursive values
**in place** where it can prove the old version is dead — a Perceus-style reuse
with no reference counting — so an accumulator threaded through a loop stays
constant-memory instead of allocating a new copy each step. Note this is done
without any hidden mutable storage: the standard library is written in pure
Ashes, and the "in-place" rewrite is a provably-safe compiler transform, not a
mutable data structure.
*Details:
[In-place reuse](docs/ARCHITECTURE.md#in-place-reuse-perceus-style-no-runtime-rc)
in the architecture guide; [Evaluation Strategy](docs/LANGUAGE_SPEC.md#15-evaluation-strategy)
in the spec.*

**Does purity make programs slower?**
Usually the opposite. Because functions have no side effects and values never
mutate, the compiler can reorder, deduplicate, and eliminate work freely, and
can safely share structure and reuse memory in place — optimizations that are
unsound in a language with aliased mutable state. Purity is also what makes the
data-parallel fold behind the compiler's own benchmarks correct: partial
results merge order-independently because there is nothing to race on.
*Details: [Evaluation Strategy](docs/LANGUAGE_SPEC.md#15-evaluation-strategy) in
the spec; the [compiler-optimization record](docs/future/COMPILER_OPTIMIZATION.md).*

**Can Ashes produce competitive native code?**
That is the goal, and it is actively benchmarked. The pipeline lowers the typed
AST to its own IR, applies whole-program analyses (move/linearity, in-place
reuse, known-call devirtualization, generic monomorphization), then emits LLVM
IR and uses LLVM's optimizer and code generator for the final native object.
The [`challenges/`](challenges/) directory tracks this with a 1BRC
implementation and the Computer Language Benchmarks Game programs (n-body,
mandelbrot, fannkuch-redux, k-nucleotide, and more).
*Details: [Compiler Architecture](docs/ARCHITECTURE.md) and the
[optimization arc](docs/future/COMPILER_OPTIMIZATION.md).*

**How are the collections implemented?**
Everything is persistent (immutable, structure-sharing):
- `List` is a singly-linked cons list — never an array.
- `Ashes.Array` is an indexed sequence backed by a persistent balanced tree, so
  `get`/`set` are `O(log n)` and `set` returns a new array sharing most nodes.
- `Ashes.Map` is a persistent AVL tree; `Ashes.HashMap` is an AVL tree ordered
  by `(hash, key)` (no caller-supplied ordering needed); `Ashes.HashTrie` is a
  persistent 16-ary hash trie — the lower-constant-factor alternative at scale.

*Details: [Standard Library](docs/STANDARD_LIBRARY.md#shipped-helper-modules).*

**Won't recursion-as-iteration overflow the stack?**
No — tail calls compile to constant-stack loops, including cross-member tail
calls in eligible `let recursive ... and ...` groups. Only non-tail recursion
consumes a frame per call and is bounded by the thread's stack (OS default on
the main thread, 1 MiB default for parallel workers).
*Details: [Tail-Call Optimization](docs/LANGUAGE_SPEC.md#183-tail-call-optimization)
in the spec; [Stacks](docs/ARCHITECTURE.md#stacks) in the architecture guide.*

**Is sharing data safe?**
Always. Every value is immutable, so the compiler shares freely instead of
copying — there is no aliasing hazard by construction. Across threads,
`Ashes.Parallel` workers run on their own arenas and results are deep-copied
back at the join, so heaps never alias between threads.
*Details: [Evaluation Strategy](docs/LANGUAGE_SPEC.md#15-evaluation-strategy)
in the spec;
[Per-thread arenas](docs/ARCHITECTURE.md#per-thread-arenas-structured-parallelism)
in the architecture guide.*

**Why don't I write ownership annotations?**
Ownership is entirely inferred. The compiler determines where each value is
created, shared, and destroyed; the language surface exposes only immutable
values. There is no move keyword, no borrow operator (`&x`), no lifetimes, and
no use-after-move errors — ownership exists purely as a compile-time
implementation detail behind deterministic cleanup.
*Details: [Ownership Model](docs/LANGUAGE_SPEC.md#17-ownership-model).*

**Where do async tasks allocate?**
A `Task` is a small heap state struct on the calling thread's arena, holding
the coroutine's captures and the variables live across each `await` — no
machine stack survives a suspension, so a pending task costs its struct, not a
stack frame. Task memory returns through the ordinary scope-based arena
reclamation, and tasks execute single-threaded on the caller's thread.
*Details: [Async/Await](docs/LANGUAGE_SPEC.md#19-asyncawait) in the spec;
[Task frames and memory](docs/ARCHITECTURE.md#task-frames-and-memory) in the
architecture guide.*

**If everything is pure, how do files, networking, and printing work?**
Through built-in functions under the reserved `Ashes` modules — `Ashes.IO`,
`Ashes.File`, `Ashes.Net`, `Ashes.Http`, `Ashes.Process`. Ashes is strictly and
sequentially evaluated, so these effects happen in a well-defined order, and the
purity contract is specifically about values: no function mutates an existing
binding or value in place. Networking and TLS additionally return `Task(E, A)`
and are consumed via `await`. A general algebraic-effects system (handlers for
`Clock`, `Random`, `FileSystem`, and the like — Ashes calls them **capabilities**) has
landed, with tail-resumptive and one-shot resumptive handlers.
*Details: [`Ashes.IO`](docs/STANDARD_LIBRARY.md#ashesio) and the built-in
modules in the standard library; the
[effects system](docs/LANGUAGE_SPEC.md#20-algebraic-effects-and-handlers).*

**Why not just use Rust?**
Aside from the obious fact that Rust is a mature and strong programming language 
while Ashes is a one person experiment that is nowhere close to production ready.. 
I'd say Ashes is very different at its core and design. Rust gives you explicit 
ownership, mutation, and low-level control. Ashes deliberately hides all of that: 
a purely functional model withHindley–Milner type inference, immutable values, 
and inferred deterministic memory management — native performance and no GC, 
but without asking the programmer to reason about ownership or lifetimes. 
If you want manual control over mutation and memory layout, reach for Rust; 
if you want to tinker with a small pure functional language that still compiles 
to a dependency-free native binary, that is what Ashes is for.
*Details: [Design Principles](#design-principles) above.*

---

## Documentation

| Document | Contents |
|---|---|
| [Development Guide](docs/DEVELOPMENT.md) | Building, testing, and developing locally |
| [Local CI/CD](docs/LOCAL_CI.md) | Containerized CI/CD pipeline and releases |
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

Ashes is the name of my beloved dog. Her enthusiasm for trying anything new
mirrors the spirit of this project - an experiment, a learning journey, and
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

---

## License

MIT — see [LICENSE](LICENSE).
