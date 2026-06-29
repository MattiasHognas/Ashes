# Automatic In-Place Reuse (Uniqueness/Linearity Analysis) — Design

Status: **Proposed (awaiting sign-off before implementation).**

Fixes FLAWS.md #2 (the hot-loop arena leak) and the O(1) half of #3, **without any new
user-visible syntax** and without violating the Ground Rules: user code stays pure and
immutable, there is no GC and no runtime reference counting. The compiler infers when a
heap value is *uniquely owned and dead*, and then reuses/overwrites its memory in place.
All mutation is internal and invisible — semantics are identical to the allocating
version, just leak-free and faster.

## 1. The problem, precisely

A `let rec loop acc = … loop(newAcc)` fold threads an accumulator. With a persistent
structure (`Ashes.Map`), each iteration's `Map.set` allocates O(log K) fresh nodes and
the superseded path nodes become garbage. The TCO back-edge cannot reset the arena
(the new map shares structure with the old), so the garbage — plus all per-line scratch
— accumulates without bound until the loop ends → OOM (`Lowering.cs:2785`,
`Lowering.Ownership.cs:330` `CanCopyOutAdt`).

The key observation: in that fold, the **old `acc` is dead the instant `loop(newAcc)` is
taken** — it is consumed exactly once and never referenced again. So its heap cells can
be *reused* to build `newAcc` instead of allocating fresh ones.

## 2. Approach — static linearity ⇒ reuse tokens (Perceus-style, but no runtime RC)

1. **Linearity analysis (new, in `Lowering.Ownership.cs`).** Identify heap values that are
   used **linearly**: consumed (pattern-matched / passed on) exactly once along every
   path, never duplicated, never captured. The TCO accumulator threaded through
   `loop(newAcc)` is the canonical case and the first target; the analysis generalises to
   any linearly-threaded value. Unlike Koka's Perceus this uses **compile-time** linearity
   (no runtime refcount — Ground Rule 6), so it is conservative: when linearity can't be
   proven, fall back to today's allocating behaviour (always correct).

2. **Reuse tokens.** When a `match` deconstructs a linear value (e.g. `Map.set`'s
   `Node(...)` case), the dead constructor cell yields a **reuse token** (its address). A
   subsequent allocation of a **same-size** constructor (`makeNode(...)`) consumes the
   token and writes into that cell instead of bump-allocating. Net: `Map.set` on a
   uniquely-owned tree rewrites the root-to-leaf path **in place** — constant footprint,
   zero garbage.

3. **Per-iteration arena reset becomes safe.** With the accumulator updated in place (no
   fresh nodes above the watermark), the TCO back-edge can reset the arena to a
   per-iteration watermark to reclaim the line/scratch allocations — extending
   `CanArenaReset`/`GetTcoCopyOutKind` to admit "linear, reused-in-place" accumulators
   instead of rejecting all pointer-bearing ADTs.

4. **Fallback: deep copy.** Where linearity fails (the value is shared/captured), correctness
   is preserved by deep-copying via `EmitDeepCopyInto(value, type)` — **the same
   type-directed deep-copy emitter #5 needs for result-copy-out on join.** Building it once
   serves both milestones.

## 3. Why this is sound under the Ground Rules

- **Purity preserved:** reuse only fires when the source value is provably dead, so no live
  value is ever observed to change. User-visible semantics are unchanged.
- **No GC / no RC:** liveness is decided at compile time by the linearity analysis; there is
  no runtime reference count or collector.
- **No user-visible Drop / no new syntax:** entirely an internal optimization; no surface
  changes.

## 4. Implementation plan (phased; each phase keeps the suite green)

1. Spec sign-off (this doc) + a `LANGUAGE_SPEC.md` note that in-place reuse is a semantics-
   preserving optimization (no observable effect).
2. `EmitDeepCopyInto(value, type)` — type-directed deep copy (strings, lists, tuples, ADTs,
   closures). Self-contained, testable via copy-then-structural-equality. **Shared with #5.**
3. Linearity analysis over the IR/AST: mark values consumed-exactly-once; start with the
   TCO-accumulator pattern, prove it on `tests/` folds.
4. Reuse-token plumbing: `match` on a linear ADT emits a reuse token; same-size `Alloc`
   consumes it (new IR: `AllocReusing(token, …)`), backend writes in place.
5. Extend the TCO back-edge (`Lowering.cs` / `Lowering.Ownership.cs`) to reset the arena
   when the accumulator is linear+reused; verify the 1BRC fold runs in constant memory.
6. Tests: a billion-row-shaped fold that now runs in bounded memory; reuse-correctness
   (results identical to the allocating path); a deliberately-shared accumulator that
   correctly falls back to copy. Update `challenges/FLAWS.md` #2/#3.

## 5. Risks / open points

- Static linearity is conservative; the first cut targets the threaded-accumulator pattern
  and may miss more complex sharing — acceptable (it only ever *adds* reuse where provably
  safe; everything else keeps working).
- Interaction with closures capturing the accumulator (capture ⇒ not linear ⇒ no reuse).
- Reuse + the existing copy-out/arena machinery must compose; phase 5 is the integration-
  risk step and is gated behind the analysis proving linearity.
- Same-size matching for reuse tokens (constructor of equal arity/layout); mismatched
  sizes fall back to fresh allocation.
