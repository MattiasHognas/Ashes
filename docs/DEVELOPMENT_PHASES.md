# Development Phases

This document defines the ordered phases for evolving Ashes from its
current pure-functional compiler into a memory-safe, high-performance
language with ownership, shared borrowing, and ergonomic pattern matching.

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes.

All 5 phases shipped as part of version `1.1.0`.

------------------------------------------------------------------------

## Completed Phases

| Phase | Name | Summary |
|-------|------|---------|
| 1 | Deterministic Resources | External resources (File, Socket) released exactly once, automatically. `Drop` IR instruction. |
| 2 | Ownership Core (Implicit Sharing) | All types classified as copy (Int, Float, Bool) or owned (String, List, Tuple, ADT, Closure). `Drop` at scope exit. |
| 3 | Compiler-Inferred Borrowing | Non-owning immutable access inferred by the compiler — no `&x` syntax, no lifetime annotations. |
| 4 | Performance & Persistent Data Structures | IR optimization pipeline (constant folding, DCE), LLVM pass bindings, allocation strategies. |
| 5 | Pattern Matching + Ergonomics | Integer/string/boolean literal patterns, let-pattern bindings, exhaustiveness checking, ownership-aware patterns. |

------------------------------------------------------------------------

## Phase 5 — Pattern Matching + Ergonomics ✅

**Status:** Complete

**Prerequisite:** Phase 4 complete

**Goal:** make the language feel great to write by improving pattern
matching expressiveness and integrating it with ownership and borrowing.

### What was delivered

| Layer | Work |
|-------|------|
| Syntax | Destructuring patterns in match (`Some(x) -> …`, `None -> …`). |
| Syntax | Integer literal patterns (`\| 0 -> …`, `\| -1 -> …`). |
| Syntax | String literal patterns (`\| "hello" -> …`). |
| Syntax | Boolean literal patterns (`\| true -> …`, `\| false -> …`). |
| Syntax | Let pattern bindings (`let (a, b) = expr in body`). |
| Syntax | Match ergonomics improvements (cleaner syntax, formatting rules). |
| Semantics | Ownership in patterns (binding moves by default, prevent unsafe partial moves). |
| Semantics | Pattern matching with inferred borrowing (match on value without moving; compiler infers borrows). |
| Semantics | Exhaustiveness checking (compiler enforces full coverage — ADTs, lists, booleans). |
| Semantics | Reachability checking (duplicate literal/constructor arms are rejected). |
| Semantics | Result/Option ergonomics (`\|?>` pipeline, `let?` binding, reduced nested matches). |
| IR | Efficient pattern matching lowering (conditional branching, no redundant checks). |
| Tests | Parsing, semantic validation, and end-to-end compilation tests for all pattern types. |
| Docs | LANGUAGE_SPEC.md §11.5–11.8 updated with all new pattern syntax. |

------------------------------------------------------------------------

## Phase Dependency Graph

```
Phase 1: Deterministic Resources           ✅
    │
    ▼
Phase 2: Ownership Core (Implicit Sharing) ✅
    │
    ▼
Phase 3: Compiler-Inferred Borrowing       ✅
    │
    ▼
Phase 4: Performance & Persistent Data     ✅
    │
    ▼
Phase 5: Pattern Matching + Ergonomics     ✅
```

After Phase 5 the core safety model is complete. The language has:

- deterministic destruction
- implicit ownership (compiler-managed, no user-visible moves)
- compiler-inferred borrowing (no `&x` syntax)
- persistent data structures and performance optimizations
- expressive, ownership-aware pattern matching

------------------------------------------------------------------------

## What Comes After

These phases complete the core language. Subsequent work focuses on
runtime and ecosystem:

| Phase | Area |
|-------|------|
| Async/Await | Async syntax and core primitives |
| Async Runtime | Scheduler and runtime support |
| Networking | HTTP and TCP layers |
| Package Manager | Ecosystem and dependency management |

------------------------------------------------------------------------

## Ground Rules (all phases)

1. **Spec first.** Update `LANGUAGE_SPEC.md` before implementing any new
   syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend.
3. **Test every invariant.** Each phase must ship with tests that prove
   the new guarantees (no double drop, no use-after-move, etc.).
4. **No user-visible `Drop`.** `Drop` is a compiler concept. Users see
   automatic cleanup.
5. **Purity preserved.** All values are immutable. There is no mutation.
   All APIs — standard library and user-defined — are pure: they return
   new values and never modify their arguments. There are no in-place
   updates visible to user code.
6. **No GC.** All resource and memory management is deterministic and
   compile-time verified.
