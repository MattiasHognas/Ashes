# Development Phases

This document defines the ordered phases for evolving Ashes from its
current pure-functional compiler into a memory-safe, high-performance
language with ownership, shared borrowing, and ergonomic pattern matching.

All 5 phases will probably ship under a single version (`1.1.0`). The phase numbers
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
| Docs | Shared references, borrowing rules, lifetime basics. |

### What does NOT change

- No lifetime annotations.

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

## Phase 4 — Performance & Persistent Data Structures

**Prerequisite:** Phase 3 complete

**Goal:** make the immutable-by-default model viable at scale. Without
mutation, every value transformation is a new allocation unless the
compiler and runtime are smart about sharing structure, eliding copies,
and reusing memory. This phase is not optional — it is core to the
language's viability.

No new user-facing syntax. Optimizations are invisible to the user.

### What changes

| Layer | Work |
|-------|------|
| Runtime | Persistent data structures — lists, maps, and sets that share unchanged structure across versions (e.g. hash-array mapped tries, finger trees). |
| Runtime | Avoid unnecessary allocations — pool or arena-allocate short-lived intermediates; fuse chained transformations where possible. |
| Runtime | Reuse memory where safe — when the compiler can prove a value has a single owner and no outstanding borrows, update in place instead of copying. |
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
| Tests | Before-vs-after output identical, persistent structure sharing correctness, edge cases with ownership/drops, tail recursion correctness. |
| Docs | Persistent data structure guarantees, optimization catalogue, invisibility guarantee, zero-cost abstraction philosophy. |

### What does NOT change

- No new syntax.
- No semantic changes.
- Observable behaviour is identical.

### Suggested order

1. Persistent data structures (lists, maps, sets).
2. Allocation avoidance (arenas, fusion).
3. Safe in-place reuse (single-owner optimization).
4. Create optimization pass pipeline.
5. Dead code elimination.
6. Constant folding.
7. Copy elision.
8. Drop elision.
9. Function inlining.
10. Tail-call optimization.
11. Redundant allocation removal.
12. Borrow-based optimizations.
13. LLVM optimization integration.
14. Tests.
15. Docs.

------------------------------------------------------------------------

## Phase 5 — Pattern Matching + Ergonomics

**Prerequisite:** Phase 4 complete

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
- Everything respects ownership/borrow rules established in Phases 2–3.

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
Phase 4: Performance & Persistent Data Structures
    │
    ▼
Phase 5: Pattern Matching + Ergonomics
```

After Phase 5 the core safety model is complete. The language has:

- deterministic destruction
- ownership and move semantics
- shared borrowing
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
6. **No GC.** All resource and memory management is deterministic and
   compile-time verified.
