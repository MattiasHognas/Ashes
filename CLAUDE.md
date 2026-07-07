# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Ashes is a **compiler** (not an application) for a pure functional ML-family language, written in
C#/.NET 10. It compiles `.ash` source to **standalone native executables** (ELF on Linux, PE on
Windows) via LLVM, with zero runtime dependencies — no GC, no runtime. Targets: `linux-x64`,
`linux-arm64`, `win-x64`.

`docs/md/` is the source of truth (also published as the documentation site; the VitePress app
lives in `docs/builder/`). Read the relevant doc **before** changing behavior:

- [docs/md/reference/language.md](docs/md/reference/language.md) — syntax/semantics, **authoritative**
- [docs/md/internals/architecture.md](docs/md/internals/architecture.md) — pipeline, backend, memory model, linking
- [docs/md/internals/ir.md](docs/md/internals/ir.md) — IR instruction set
- [docs/md/reference/cli.md](docs/md/reference/cli.md) — all CLI commands and flags
- [docs/md/reference/formatter.md](docs/md/reference/formatter.md) — canonical formatting rules
- [docs/md/reference/diagnostics.md](docs/md/reference/diagnostics.md) — error codes and messages
- [docs/md/guide/testing.md](docs/md/guide/testing.md) — test directives and conventions
- [docs/md/reference/standard-library.md](docs/md/reference/standard-library.md) — module-by-module API
- [docs/md/guide/projects.md](docs/md/guide/projects.md) — multi-file project / `ashes.json` format
- [docs/md/guide/development.md](docs/md/guide/development.md) — building, testing, developing locally
- [docs/md/guide/debugging.md](docs/md/guide/debugging.md) — debug extension setup and usage
- [docs/md/guide/local-ci.md](docs/md/guide/local-ci.md) — containerized local CI/CD (`just` + Podman), release flow
- [docs/md/future/FUTURE_FEATURES.md](docs/md/future/FUTURE_FEATURES.md) — planned work (incl. memory/ownership roadmap)

If implementation conflicts with the spec, **update the spec first**.

## One-time setup

Backend and end-to-end tests need LLVM native runtimes. Download them before running anything
backend-related:

```bash
bash scripts/download-llvm-native.sh --all   # provisions Linux x64, Linux arm64, Windows x64
```

The rustls-ffi TLS payloads (used by `Ashes.Net.Tls` / `Ashes.Http`) are vendored under
`runtimes/` and normally don't need fetching. Refresh them only when `<RustlsFfiVersion>` in
`Directory.Build.props` changes:

```bash
bash scripts/download-rustls-ffi.sh --all    # linux-x64/win-x64 are downloaded; linux-arm64 is source-built
```

The openlibm bitcode payloads (used by `Ashes.Math` transcendentals) are likewise vendored under
`runtimes/` and normally don't need fetching. Refresh them only when `<OpenlibmVersion>` changes:

```bash
bash scripts/download-openlibm.sh --all       # builds libopenlibm.bc for all targets on one host (needs clang, llvm-link, opt)
```

The PCRE2 bitcode payloads (used by `Ashes.Regex`) are likewise vendored under `runtimes/` and
normally don't need fetching. Refresh them only when `<Pcre2Version>` changes:

```bash
bash scripts/download-pcre2.sh --all          # builds libpcre2.bc (8-bit, Unicode, JIT off) for all targets on one host (needs clang, llvm-link, opt, llvm-nm)
```

All four scripts accept per-target flags (`--linux-x64`, `--linux-arm64`, `--win-x64`) instead of
`--all`. linux-arm64 rustls has no upstream prebuilt, so it's cross-compiled (needs cargo, rustup,
and an aarch64 GNU linker; the script auto-installs the cross-linker on apt/pacman systems). openlibm
is compiled to LLVM bitcode by the clang frontend, so every target builds on one host with no cross
toolchain.

## Build, test, format

After **any** change, all of these must pass (also enforced by CI / `scripts/verify.sh`):

```bash
dotnet build Ashes.slnx

# C# compiler-internal tests (deterministic)
dotnet run --project src/Ashes.Tests -- --no-progress

# LSP-internal tests
dotnet run --project src/Ashes.Lsp.Tests -- --no-progress

# End-to-end .ash tests (needs LLVM runtimes + built CLI)
dotnet run --project src/Ashes.Cli -- test tests

# C# formatting (CI-enforced; TreatWarningsAsErrors + analyzers are on)
dotnet format Ashes.slnx --verify-no-changes   # use `dotnet format Ashes.slnx` to fix
```

Run a **single** end-to-end test by pointing `test` at a file: `dotnet run --project src/Ashes.Cli -- test tests/foo.ash`.
Filter the **C# unit tests** to one class with `--treenode-filter`:
`dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/ClassName/**"`.

In C# tests, **`[NotInParallel]` is banned** — fix the root cause of the race instead of serializing.

After creating/modifying any `.ash` file (including stdlib and examples), canonically format it —
formatting is part of correctness, not style:

```bash
dotnet run --project src/Ashes.Cli -- fmt <path> -w
```

`scripts/verify.sh` runs the full gate (build, format, all test layers, publish self-contained CLI,
format+run every example/test, and build the VS Code extension).

### Running non-native targets on a Linux host

Backend/runtime validation for the other targets can run on a Linux x64 host via emulation:

- **win-x64**: executed through `wine64` (or `wine`) when the test helper is wired for PE execution.
  The Windows TLS runtime uses the platform verifier by default; tests may set `SSL_CERT_FILE` to
  load PEM roots explicitly, enabling Wine-backed loopback TLS coverage on Linux.
- **linux-arm64**: executed through `qemu-aarch64` / `qemu-aarch64-static` with an arm64 sysroot
  (e.g. `/usr/aarch64-linux-gnu`). The linux-arm64 coverage helper looks for qemu on `PATH` and at
  the rootless Arch-style location `~/.local/share/ashes-tools/qemu-user-static/root/usr/bin`.

## CLI entry points

`dotnet run --project src/Ashes.Cli -- <cmd>` where `<cmd>` is: `compile` (`--target <rid>`,
`--debug`, `-o`), `run` (`-- arg1 arg2` to pass args), `repl`, `test`, `fmt`, `init`, `add`,
`remove`, `install`.

## Architecture — projects and the strict dependency DAG

The pipeline is split into phases. **Dependencies are enforced and must not be violated:**

| Project | Responsibility | May depend on |
|---|---|---|
| `Ashes.Frontend` | Lexer, parser, AST | (nothing) |
| `Ashes.Semantics` | Binding, scope resolution, HM type inference, IR lowering | Frontend |
| `Ashes.Backend` | IR → LLVM native codegen, linking | Semantics |
| `Ashes.Formatter` | Canonical AST → source formatting | Frontend |
| `Ashes.TestRunner` | End-to-end `.ash` test execution | Backend |
| `Ashes.Lsp` | Language server (diagnostics, hover, completion, formatting) | Frontend, Semantics, Formatter — **NOT Backend** |
| `Ashes.Dap` | Debug Adapter Protocol server (gdb/lldb) | (nothing — protocol adapter only) |
| `Ashes.Cli` | Orchestration/UX only | Backend, Formatter, TestRunner |
| `Ashes.Tests`, `Ashes.Lsp.Tests` | may reference any project |

Semantics lowering is the bulk of the work — it is split across `Lowering.*.cs` partial classes
(Builtins, Patterns, TypeInference, Types, Symbols, Ownership, Diagnostics) plus `Ir.cs`,
`IrOptimizer.cs`, `BuiltinRegistry.cs`, `StateMachineTransform.cs` (async/await lowering).

**Boundary rules:** Lsp and Dap are *consumers* of compiler logic, never implementers. Do not
duplicate parsing, type inference, lowering, or codegen in them, and do not implement runtime
behavior in Frontend. All semantics originate in the compiler phases.

## Language invariants (do not break)

Ashes is pure, immutable, expression-based, strictly evaluated, recursion-based. There is **no**
mutation, reassignment, loops, statements, or null. Iteration is recursion + `match`. Lists are
immutable linked lists, never arrays. Never collapse `match` into if-chains. Type system is
Hindley-Milner with let-polymorphism. Don't add syntax/evaluation/typing rules without updating
`LANGUAGE_SPEC.md` first.

**Top-level declarations:** a file is `import* declaration* expr?` — a flat sequence of top-level
`let` / `let recursive ... and ...` / `type` / `external` declarations (no trailing `in`) followed by an
optional trailing expression. Scoping is sequential (Model A): a binding is visible to subsequent
declarations and the trailing expression, never to earlier ones; self-recursion needs `let recursive`,
mutual recursion needs `let recursive X = ... and Y = ...`. Imports support whole-module (`import M`,
`import M as X`) and selector forms (`import M.binding [as x]`, `import M.Type [as T]`) that bring
the name in unqualified, with built-in `Ashes.*` modules resolving via the same path. A module's
exports are its top-level `let`/`type` declarations only — `external` and the trailing expression are
never exported, and there is no implicit re-export. The bare-expression and nested `let ... in`
pyramid styles both remain valid. Diagnostics `ASH013`–`ASH016` cover this surface (see
[docs/md/reference/diagnostics.md](docs/md/reference/diagnostics.md)).

**Memory model:** no GC and no reference counting — memory is managed by deterministic destruction,
with ownership + borrowing planned (`Lowering.Ownership.cs` is the in-progress home for this; see
[docs/md/future/FUTURE_FEATURES.md](docs/md/future/FUTURE_FEATURES.md)). Don't reach for GC/RC-style designs.

The standard library is written in Ashes under `lib/Ashes/` (e.g. `List.ash`, `IO.ash`); `dist/`
holds the shipped per-target copies. End-to-end tests live in `tests/*.ash` as ordinary programs with a leading `//` directive block
(see [docs/md/guide/testing.md](docs/md/guide/testing.md) for the full surface): `// expect:` (exact stdout, default exit 0),
`// expect-compile-error:` (substring match, exit 1), `// exit: N`, `// stdin:`, `// file:`/
`// file-bytes:`, and `// tcp-server`/`// tcp-expect`/`// tcp-send` loopback fixtures. Gotcha:
`// expect: empty` expects the literal text `empty`, not empty output (matching trims trailing
whitespace, otherwise exact). `// fmt-skip:` exempts an intentionally malformed fixture from
formatting checks. Discovery goes into project mode when an `ashes.json` is found upward. Examples
live in `examples/` and must **not** use test directives.

## Feature implementation flow

1. Update `LANGUAGE_SPEC.md` if behavior changes. 2. Frontend → 3. Semantics → 4. Backend →
5. Diagnostics → 6. `.ash` tests + examples → 7. format all changed `.ash` and verify no diffs.
Implement only what the active issue/milestone specifies; if behavior is undefined, stop and leave a
TODO rather than inventing it. Development is milestone-driven — avoid speculative scope.
