# Development Phases

This document defines the ordered phases for evolving Ashes from its
current pure-functional compiler into a memory-safe, high-performance
language with ownership, shared borrowing, and ergonomic pattern matching.

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes.

All 5 phases will probably ship under a single version (`1.1.0`). The phase numbers
exist only to communicate dependency order and guide development
sequencing.

Each phase builds strictly on the previous one. No phase may be started
until its predecessor is complete and tested.

------------------------------------------------------------------------

## Phase 1 — Deterministic Resources ✅

**Status:** Complete

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

## Phase 2 — Ownership Core (Implicit Sharing) ✅

**Status:** Complete

**Prerequisite:** Phase 1 complete

**Goal:** classify all types as either *copy* or *owned* so the compiler
can insert deterministic cleanup for every heap-allocated value — not
just resources.

Ashes uses an **implicit sharing** model: values are shared by default,
the compiler infers when to borrow or copy, and "move" is an
optimisation detail invisible to user code.  There is no borrow
operator, no move syntax, and no use-after-move errors.

### What changes

| Layer | Work |
|-------|------|
| Semantics | Classify owned vs copy types. Owned: String, List, Tuples, ADTs, Closures. Copy: Int, Float, Bool. |
| Semantics | Extend `Drop` to all owned values (insert at scope end for let bindings and match branches). |
| Semantics | Generalise resource tracking into ownership tracking (OwnershipInfo replaces ResourceInfo). |
| IR | `Drop` instruction carries a `TypeName` (was `ResourceTypeName`). |
| Backend | Extend drop lowering: resource types invoke platform cleanup; other owned types are no-ops in the linear allocator (placeholder for Phase 4 `free`). |
| Tests | Type classification, correct drop insertion for every owned type, no drops for copy types. |
| Docs | Owned vs copy types, implicit sharing model, deterministic cleanup scope. |

### What does NOT change

- No borrow syntax — borrowing is inferred by the compiler.
- No move syntax — moves are a compiler optimisation, not user-visible.
- No use-after-move errors — values can be used freely; the compiler
  handles sharing transparently.
- No lifetimes.
- Backend allocator unchanged (linear/bump); actual `free` deferred to
  Phase 4.

### Mental model

After this phase every heap-allocated value has a single owning scope
that will emit a `Drop` when the scope exits.  Users never write
cleanup, never think about moves, and never see ownership errors for
non-resource types.  The model is closer to Swift's value semantics than
Rust's affine types.

### Suggested order

1. Classify owned vs copy types.
2. Generalise resource tracking → ownership tracking.
3. Extend `Drop` insertion to all owned types.
4. Backend: handle new type names (no-op for non-resource).
5. Tests.
6. Docs.

------------------------------------------------------------------------

## Phase 3 — Compiler-Inferred Borrowing ✅

**Status:** Complete

**Prerequisite:** Phase 2 complete

**Goal:** allow the compiler to infer non-owning, immutable access to
values so they can be shared efficiently without user annotation.

Borrowing is **compiler-inferred** — there is no `&x` syntax.  The
compiler analyses usage and automatically borrows where safe.

### What changes

| Layer | Work |
|-------|------|
| Semantics | Inferred borrow analysis: detect when a binding is only read, not consumed. |
| Semantics | Scope-based lifetime tracking (no annotations). |
| Semantics | Ensure values are not dropped while a borrow is live. |
| IR | Represent borrowed values (tagged reference / raw pointer — internal). |
| Backend | Borrow representation in LLVM (no ownership transfer, no drop responsibility). |
| Tests | Inferred borrows, multiple borrows, borrow lifetime correctness. |
| Docs | Inferred borrowing model, lifetime basics, no user syntax. |

### What does NOT change

- No borrow syntax (`&x`) — borrowing is always inferred.
- No lifetime annotations.
- No user-visible borrow errors for pure values.

### Suggested order

1. Inferred borrow analysis in Semantics.
2. Scope-based lifetime tracking.
3. Drop interaction with borrows.
4. IR representation.
5. Backend representation.
6. Tests.
7. Docs.

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
| Semantics | Pattern matching with inferred borrowing (match on value without moving; compiler infers borrows). |
| Semantics | Exhaustiveness checking (compiler enforces full coverage). |
| Semantics | Result/Option ergonomics (integrate with `|?>` pipeline, reduce nested matches). |
| IR | Improved pattern matching lowering (efficient branching, no redundant checks). |
| Tests | Destructuring, ownership in patterns, inferred borrowing in patterns, exhaustiveness. |
| Docs | Pattern syntax, ownership behavior, match ergonomics, Result/Option workflows. |

### What does NOT change

- No major new safety concepts.
- Everything respects ownership/borrowing rules established in Phases 2–3.
- Borrowing remains compiler-inferred (no explicit borrow syntax).

### Suggested order

1. Destructuring patterns.
2. Let pattern bindings.
3. Ownership in patterns.
4. Pattern matching with inferred borrowing.
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
Phase 2: Ownership Core (Implicit Sharing)
    │
    ▼
Phase 3: Compiler-Inferred Borrowing
    │
    ▼
Phase 4: Performance & Persistent Data Structures
    │
    ▼
Phase 5: Pattern Matching + Ergonomics
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
