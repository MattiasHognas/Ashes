# Ashes – Copilot Instructions

Ashes is a pure functional programming language compiler written in .NET.

This repository is a compiler, not an application.

---

# Language Specification

The authoritative surface syntax and semantics for Ashes are defined in:

docs/LANGUAGE_SPEC.md

When implementing or modifying language features (parsing, type inference,
pattern matching, lists, etc.), follow LANGUAGE_SPEC.md exactly.

Do not introduce:

- new syntax
- new evaluation behavior
- new typing rules

unless LANGUAGE_SPEC.md is updated first.

If implementation conflicts with the spec, update the spec before code.

---

# Compiler Architecture

Ashes is split into language phases:

| Project | Responsibility |
|--------|----------------|
| Ashes.Frontend | Tokenization, parsing, AST |
| Ashes.Semantics | Binding, scope resolution, type inference |
| Ashes.Backend | IR lowering and native code generation |
| Ashes.Formatter | Formatting only (AST → formatted source) |
| Ashes.TestRunner | End-to-end `.ash` tests |
| Ashes.Cli | Orchestration only (commands, UX, wiring) |
| Ashes.Lsp | Language Server (diagnostics, formatting, completion) |
| Ashes.Tests | Compiler-internal tests |
| Ashes.Lsp.Tests | LSP-internal tests |

---

# Dependency Rules

- Ashes.Frontend has no dependencies on other Ashes projects.
- Ashes.Semantics depends on Ashes.Frontend only.
- Ashes.Backend depends on Ashes.Semantics.
- Ashes.Formatter depends on Ashes.Frontend only.
- Ashes.TestRunner depends on Ashes.Backend.
- Ashes.Lsp depends on Frontend + Semantics (+ Formatter).
- Ashes.Lsp must NOT depend on Ashes.Backend.
- Ashes.Cli may depend on Backend/Formatter/TestRunner only.
- Ashes.Tests may reference any project.
- Ashes.Lsp.Tests may reference any project.

---

# Language Model

Ashes is:

- pure
- immutable
- expression-based
- strictly evaluated
- recursion-based

Ashes has:

- no mutation
- no reassignment
- no loops
- no statements

Iteration is expressed via recursion and pattern matching.

---

# Do NOT Introduce

Copilot must never:

- introduce mutation
- introduce nulls
- treat lists as arrays
- collapse `match` into if-chains
- implement pop/push mutation
- add loops
- bypass Semantics when adding syntax
- implement runtime behavior in Frontend

Lists must remain immutable linked lists.

---

# Formatting Requirements (REQUIRED)

All `.ash` source files MUST be canonically formatted using the Ashes formatter.

After creating or modifying any `.ash` file, run:

dotnet run --project src/Ashes.Cli -- fmt <path> -w

Changes are not complete until formatting produces no further diffs.

Formatting is considered part of correctness, not style.

---

# Runtime Prerequisites

Before running backend or end-to-end tests, download all LLVM native runtimes:

```bash
bash scripts/download-llvm-native.sh --all
```

This provisions Linux x64, Linux arm64, and Windows x64 LLVM libraries.

---

# Tests

There are two test layers:

1) Ashes.TestRunner + `tests/*.ash`
   - End-to-end language tests using `// expect:`.

2) Ashes.Tests
   - Compiler-internal deterministic tests.

All new language features must include:

- `.ash` tests
- `// expect:` assertions
- full end-to-end compilation

---

# Implementation Flow

When implementing a feature:

1. Update LANGUAGE_SPEC.md (if needed)
2. Update Frontend
3. Update Semantics
4. Update Backend
5. Add diagnostics
6. Add `.ash` tests and examples
7. Format all changed `.ash` files and verify no diffs remain

---

# LSP Boundary Rules

Ashes.Lsp is a consumer of compiler logic, not an implementation of language behavior.

Allowed:
- Request analysis from compiler layers
- Convert diagnostics to LSP messages
- Provide formatting via Ashes.Formatter

Forbidden:
- Implement parsing logic
- Duplicate type inference
- Guess types heuristically
- Implement syntax validation independently

All semantics must originate from compiler phases.

---

# Milestone & Issue Discipline

Ashes development is milestone-driven.

Copilot must:

- implement only what the active issue specifies
- avoid speculative or future milestone features
- avoid expanding APIs beyond scope

If required behavior is undefined:

- stop
- leave a TODO
- do not invent behavior

---

# C# Formatting (REQUIRED)

All C# source files must pass the .NET formatter.

After making any C# changes, run:

```bash
dotnet format Ashes.slnx --verify-no-changes
```

If it reports errors, fix them with:

```bash
dotnet format Ashes.slnx
```

This check is enforced by CI alongside the test suite.

---

# Copilot Behavioral Contract

Copilot acts as a compiler contributor, not a prototype generator.

A task is complete only when:

1. Code compiles
2. Spec matches implementation
3. Tests added/updated
4. Tests pass end-to-end
5. `.ash` files formatted
6. C# formatting verified (`dotnet format Ashes.slnx --verify-no-changes`)
7. No architectural rules violated

---

# Repository Philosophy

Ashes prioritizes:

1. correctness over convenience
2. clarity over cleverness
3. explicit behavior over implicit magic
4. small composable features over large abstractions

## Memory Safety Roadmap

Ashes explicitly plans:
- no GC
- no RC
- deterministic destruction
- ownership + borrowing

See docs/FUTURE_FEATURES.md for planned features.

