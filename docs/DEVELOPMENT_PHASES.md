# Development Phases

This document defines the ordered phases for evolving Ashes from its
current pure-functional compiler into a memory-safe, high-performance
language with ownership, borrowing, and ergonomic pattern matching.

All phases ship under a single version (`1.1.0`). The phase numbers
exist only to communicate dependency order and guide development
sequencing.

Each phase builds strictly on the previous one. No phase may be started
until its predecessor is complete and tested.

------------------------------------------------------------------------

## Phase 1 — Deterministic Resources

**Prerequisite:** stable LLVM backend (current state)

**Goal:** ensure external resources (files, sockets) are released exactly
once, automatically, without garbage collection.

This phase introduces destruction semantics without introducing
ownership complexity.

### What changes

| Layer | Work |
|-------|------|
| Semantics | Classify resource types (File, Socket). Internal only — not user-visible. |
| Semantics | Insert `Drop` calls at end of scope for every resource binding. |
| Semantics | Insert `Drop` on all control-flow paths (if/then/else, match, early returns). |
| Semantics | Reject use-after-drop (resource used after explicit close). |
| Semantics | Reject double-drop (resource closed twice). |
| IR | Add `Drop` instruction (value → void, no-op for non-resource types). |
| Backend | Lower `Drop` to runtime calls (`file_close`, `socket_close`). |
| Std | Define runtime drop functions. Compiler guarantees single invocation; functions may assume exactly-once semantics. |
| Tests | Resource lifecycle: scope drop, control-flow drop, no double drop, no use-after-drop. |
| Docs | Describe resource types, automatic cleanup, no GC. Do NOT expose `Drop` as user syntax. |

### What does NOT change

- No move semantics.
- No borrowing.
- No ownership of normal values (String, List, ADTs).
- Pure values (Int, Bool, String, ADTs) are unaffected.

### Suggested order

1. Classify resource types in Semantics.
2. Add `Drop` IR instruction.
3. Insert `Drop` at end of scope (simple case).
4. Insert `Drop` on all control-flow paths.
5. Reject use-after-drop.
6. Reject double-drop.
7. Define runtime drop functions in Std.
8. Lower `Drop` in Backend.
9. Tests.
10. Docs.

### Mental model

After this phase Ashes behaves like Go/C# `using` blocks, but automatic.
Not yet like Rust.

------------------------------------------------------------------------

## Phase 2 — Ownership Core

**Prerequisite:** Phase 1 complete

**Goal:** introduce move semantics for heap-allocated values so every
owned value is dropped exactly once.

### What changes

| Layer | Work |
|-------|------|
| Semantics | Classify owned vs copy types. Owned: String, List, ADTs, Closures. Copy: Int, Bool. |
| Semantics | Move semantics — assigning an owned value moves it; original binding becomes invalid. |
| Semantics | Use-after-move errors. |
| Semantics | Extend `Drop` to all owned values (insert at scope end, for intermediates, respect moves). |
| Semantics | Function ownership rules: passing owned → moves; returning owned → transfers ownership; copy types unchanged. |
| IR | Ownership-aware drop insertion (do not drop moved values). |
| Backend | Extend drop lowering for String, List, ADTs, Closures. |
| Tests | Move semantics, use-after-move, correct drop insertion, function ownership. |
| Docs | Owned vs copy types, move semantics, drop behavior. No borrowing yet. |

### What does NOT change

- No borrowing.
- No lifetimes.
- No mutable references.

### Suggested order

1. Classify owned vs copy types.
2. Implement move semantics.
3. Use-after-move errors.
4. Extend `Drop` insertion.
5. Function ownership rules.
6. Ownership-aware IR drop insertion.
7. Backend drop lowering for all owned types.
8. Tests.
9. Docs.

------------------------------------------------------------------------

## Phase 3 — Borrowing (Shared References)

**Prerequisite:** Phase 2 complete

**Goal:** allow non-owning, immutable access to values so they can be
reused without moves.

### What changes

| Layer | Work |
|-------|------|
| Syntax | Borrow operator `&x`. |
| Semantics | Shared borrow rules: no move, multiple borrows allowed, borrow must not outlive owner. |
| Semantics | Prevent move while borrowed. |
| Semantics | Basic lifetime tracking (scope-based, no annotations). |
| Semantics | Ensure values are not dropped while borrowed. |
| IR | Represent borrowed values (tagged reference / raw pointer). |
| Backend | Borrow representation in LLVM (no ownership transfer, no drop responsibility). |
| Tests | Shared borrows, multiple borrows, invalid move during borrow, lifetime violations. |
| Docs | Shared references, borrowing rules, lifetime basics, limitations (no mutation yet). |

### What does NOT change

- No mutable references.
- No lifetime annotations.
- No mutation through references.

### Suggested order

1. Borrow operator syntax.
2. Shared borrow rules in Semantics.
3. Prevent move while borrowed.
4. Basic lifetime tracking.
5. Drop interaction with borrows.
6. IR representation.
7. Backend representation.
8. Tests.
9. Docs.

------------------------------------------------------------------------

## Phase 4 — Pure Performance Optimizations

**Prerequisite:** Phase 3 complete

**Goal:** improve runtime performance using guarantees from ownership and
borrowing. No new syntax, no semantic changes. Optimizations are
invisible to the user.

### What changes

| Layer | Work |
|-------|------|
| IR | Optimization pass pipeline (multiple passes, explicit ordering, runs after semantic lowering, before backend). |
| Optimization | Dead code elimination. |
| Optimization | Constant folding. |
| Optimization | Copy elision (avoid unnecessary copies/moves). |
| Optimization | Drop elision (skip drops for moved, uninitialized, or optimized-away values). |
| Optimization | Inline small pure functions. |
| Optimization | Tail-call optimization (constant stack for tail-recursive functions). |
| Optimization | Remove redundant allocations (short-lived ADTs, temporary closures). |
| Optimization | Borrow-based optimizations (avoid copies when references suffice). |
| Backend | Integrate LLVM optimization passes. |
| Tests | Before-vs-after output identical, edge cases with ownership/drops, tail recursion correctness. |
| Docs | Optimization catalogue, invisibility guarantee, zero-cost abstraction philosophy. |

### What does NOT change

- No new syntax.
- No semantic changes.
- Observable behaviour is identical.

### Suggested order

1. Create optimization pass pipeline.
2. Dead code elimination.
3. Constant folding.
4. Copy elision.
5. Drop elision.
6. Function inlining.
7. Tail-call optimization.
8. Redundant allocation removal.
9. Borrow-based optimizations.
10. LLVM optimization integration.
11. Tests.
12. Docs.

------------------------------------------------------------------------

## Phase 5 — Mutable Borrows

**Prerequisite:** Phase 4 complete (optimizations stabilized)

**Goal:** enable controlled in-place mutation through exclusive mutable
references while preserving memory safety.

### What changes

| Layer | Work |
|-------|------|
| Syntax | Mutable borrow operator `&mut x`. |
| Semantics | Mutable borrow rules: one `&mut` at a time, no `&` while `&mut` exists, no move of the value. |
| Semantics | Prevent aliasing violations (no `&mut` + `&`, no `&mut` + `&mut`). |
| Semantics | Mutation only through `&mut` (owned values remain immutable unless mutably borrowed). |
| Semantics | Lifetime tracking for mutable borrows (borrow must end before reuse). |
| Semantics | Ensure values are not dropped while mutably borrowed. |
| IR | Distinguish owned values, shared references, and mutable references. |
| Backend | LLVM representation of mutable borrows (pointer-like, no drop responsibility). |
| Tests | Single mutable borrow, aliasing violations, mutation correctness, lifetime enforcement. |
| Docs | Mutable reference rules, aliasing restrictions, mutation model, differences from shared borrowing. |

### What does NOT change

- No new safety concepts beyond exclusive mutability.
- Owned values remain immutable by default.
- No GC, no runtime checks.

### Suggested order

1. Mutable borrow operator syntax.
2. Mutable borrow rules.
3. Aliasing violation checks.
4. Mutation through `&mut`.
5. Lifetime tracking for mutable borrows.
6. Drop interaction with mutable borrows.
7. IR representation.
8. Backend representation.
9. Tests.
10. Docs.

------------------------------------------------------------------------

## Phase 6 — Pattern Matching + Ergonomics

**Prerequisite:** Phase 5 complete

**Goal:** make the language feel great to write by improving pattern
matching expressiveness and integrating it with ownership and borrowing.

### What changes

| Layer | Work |
|-------|------|
| Syntax | Destructuring patterns in match (`Some(x) -> …`, `None -> …`). |
| Syntax | Let pattern bindings (`let Some(x) = expr`). |
| Syntax | Match ergonomics improvements (optional parentheses, cleaner syntax, formatting rules). |
| Semantics | Ownership in patterns (binding moves by default, prevent unsafe partial moves). |
| Semantics | Pattern matching with borrowing (match on `&value` without moving). |
| Semantics | Exhaustiveness checking (compiler enforces full coverage). |
| Semantics | Result/Option ergonomics (integrate with `|?>` pipeline, reduce nested matches). |
| IR | Improved pattern matching lowering (efficient branching, no redundant checks). |
| Tests | Destructuring, ownership in patterns, borrowing in patterns, exhaustiveness. |
| Docs | Pattern syntax, ownership behavior, match ergonomics, Result/Option workflows. |

### What does NOT change

- No major new safety concepts.
- Everything respects ownership/borrow rules established in Phases 2–5.

### Suggested order

1. Destructuring patterns.
2. Let pattern bindings.
3. Ownership in patterns.
4. Pattern matching with borrowing.
5. Match ergonomics improvements.
6. Exhaustiveness checking.
7. Result/Option ergonomics.
8. IR lowering improvements.
9. Tests.
10. Docs.

------------------------------------------------------------------------

## Phase Dependency Graph

```
Phase 1: Deterministic Resources
    │
    ▼
Phase 2: Ownership Core
    │
    ▼
Phase 3: Borrowing (Shared References)
    │
    ▼
Phase 4: Pure Performance Optimizations
    │
    ▼
Phase 5: Mutable Borrows
    │
    ▼
Phase 6: Pattern Matching + Ergonomics
```

After Phase 6 the core safety model is complete. The language has:

- deterministic destruction
- ownership and move semantics
- shared and mutable borrowing
- optimizations that leverage safety guarantees
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
5. **Purity preserved.** Mutation is only allowed through `&mut`
   (Phase 5+). All other values remain immutable.
6. **No GC.** All resource and memory management is deterministic and
   compile-time verified.
