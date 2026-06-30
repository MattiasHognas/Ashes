# Automatic In-Place Reuse (Uniqueness/Linearity Analysis) — Design

Status: **In progress — direct-accumulator in-place reuse landed & verified; `Map.set`-group specialization (indirect reuse) is the remaining work. 2026-06-30.**

> **DONE — direct-accumulator reuse.** When a TCO loop body directly `match`es a recursive-ADT
> accumulator and rebuilds it with the same constructor, the deconstructed node is now overwritten
> in place (`AllocReusing`) instead of reallocated. Soundness without runtime refcounting comes from
> a one-time **defensive deep copy of the accumulator at loop entry** (makes the loop-local
> accumulator uniquely owned regardless of caller sharing) + the reuse only firing in arms that
> don't reference the accumulator again (cell is dead). Pieces:
> - `IrInst.AllocReusing` + `EmitAllocReusing` (overwrite tag, no bump alloc).
> - `_linearReuseNames` / `_reuseTokens` in `Lowering`; token produced in both match-arm lowering
>   paths (`LowerMatchArmsLinear` / `LowerMatchArmsViaTagSwitch`) for a linear scrutinee, consumed by
>   a same-arity constructor in `LowerConstructorApplication` (`TryConsumeReuseToken`).
> - Eligibility (`CollectCtorMatchedScrutinees`) + deferred defensive copy spliced in at loop entry
>   (emitted after the body so HM has resolved the accumulator type; type taken from the matched
>   constructor). Restricted to non-copy-out-able (pointer-bearing/recursive) ADTs — copy-type ADTs
>   are already bounded by the existing shallow copy-out. Per-frame save/restore in `LowerLambdaCore`.
> - Verified: a `Node`-tree fold runs 50M iterations in ~4 MB constant memory (≈1.6 GB without
>   reuse), correct results; a caller-shared initial accumulator is **not** corrupted
>   (`tests/reuse_inplace_accumulator.ash`). Whole suite green: 1307 unit + 354 e2e.
>
> **REMAINING — indirect reuse (the 1BRC `Map.set` fold).** When the accumulator is updated by a
> *called* function (`loop(Map.set(…)(acc))`) the reuse must happen inside `Map.set`. That needs the
> linearity-driven **specialization** below: a `…$reuse` clone of the `Map.set`/`balance`/`rotate`/
> `makeNode` group (the loop's accumulator is unique by the same entry deep copy) with the reuse
> token threaded from `set$reuse`'s match into `makeNode$reuse`'s construction. The direct-case
> machinery above (`AllocReusing`, token plumbing, defensive copy, eligibility) is the foundation it
> builds on.
>
> **Concrete target / discriminator:** the minimal indirect case is a rebuild via a non-recursive
> helper — `let mk l v r = Node(l)(v)(r)` then `loop(n-1)(mk(l)(v+n)(r))`. Today that **leaks
> ~240 MB** at 2M iters where the direct `Node(l)(v+n)(r)` form reuses to ~4 MB. Drive that helper
> case to ~4 MB first; `Map.set` is the same shape with more helpers.
>
> **Obstacles confirmed this session (why it's a multi-day effort, not an afternoon):**
> 1. **No function-AST registry.** Top-level / stdlib function bodies are lowered and discarded;
>    generating a `$reuse` variant means re-lowering the callee's AST, so a name→AST map of top-level
>    `let`s (user + embedded stdlib) must be built and threaded into lowering first.
> 2. **Curried closure-application calls.** `mk(l)(v)(r)` lowers to nested `CallClosure`s, not a
>    direct call. Threading a reuse token into the callee means recognising the *saturated* call and
>    emitting a direct call to the `$reuse` variant with the token as an extra argument — a new
>    special-case call path, plus the variant generation, plus call rewiring.
> 3. **Intermediate-value linearity (the real killer for bounded `Map.set`).** Each rebuild level is
>    `balance(makeNode(go(left))(key)(value)(right))`: `makeNode` builds a node that `balance`
>    immediately matches and supersedes with its own `normalized = makeNode(…)`. For *constant*
>    memory both must reuse — the cell must flow old-node → `makeNode` → `normalized`. Reusing only
>    the matched accumulator node (what the direct-case machinery does) still leaves the `normalized`
>    + rotation nodes as fresh allocations on the new map's path, so the arena still can't reset →
>    still O(log K)/set leaked. This needs tracking that a *freshly-built, used-once, then-matched*
>    value is unique (local linearity), in addition to the accumulator linearity.
> 4. **Recursive specialization through `balance`/`rotate`.** `set$reuse` → `makeNode$reuse`,
>    `balance$reuse` → `rotateLeft$reuse`/`rotateRight$reuse`/`makeNode$reuse`, each threading tokens
>    for the dead nodes they match. A single wrong token along a rebalancing path is silent
>    use-after-reuse corruption that is hard to exhaustively stress-test.

> **What's done:** the `IrInst.AllocReusing(Target, Tag, FieldCount, TokenTemp)` primitive +
> backend (`EmitAllocReusing`: overwrite the dead cell's tag, no bump alloc) + optimizer wiring.
> This is the "write into the reuse token" half of Perceus. Unwired into lowering yet — it needs
> the analysis below to know *when* a cell is a safe token.
>
> **The real blocker (worked out this session):** reuse is only sound if the matched cell is
> **uniquely owned and dead**. Ashes forbids runtime refcounting (Ground Rule 6), so uniqueness
> must be **compile-time**. The hard fact: in `loop(Map.set(…)(acc))`, `acc` is *not* provably
> unique — the caller of `loop` may still hold the initial map (`let m = … in loop(m); use m`),
> so iteration 1's `acc` can be shared. Reusing it would corrupt the caller's `m`. A purely local
> "`acc` is dead after this call" check is unsound (it can't see the caller's sharing).
>
> **The sound design (the achievable path):**
> 1. **Defensive copy at loop entry.** Deep-copy the initial accumulator *once* before the loop
>    body label (O(K) once, via the existing `EmitDeepCopy`). Now the loop's accumulator region is
>    unique regardless of the caller — this is what makes per-iteration reuse sound.
> 2. **Reuse transformation.** In a function body, a `match v with Ctor(fields) -> arm` where `v`
>    is dead in `arm` (not in `FreeVars(arm)`) and `arm` builds a same-arity constructor ⇒ emit
>    `AllocReusing(v's cell)` for that construction (the token-availability dataflow). This makes
>    `Map.set`'s `match map … makeNode(…)` reuse the node it just deconstructed.
> 3. **Specialization (the substantial interprocedural part).** Because the transformed body
>    *mutates* its input, it's only safe for unique callers. Clone the `Map.set`/`balance`/`rotate`/
>    `makeNode`/`rotateLeft`/`rotateRight` group into `…$reuse` variants; the loop (whose accumulator
>    is unique by step 1) calls `set$reuse`, the recursion/helpers call each other's `$reuse`
>    variants, and ordinary callers keep calling the normal (allocating) versions. The reuse token
>    must thread from `set$reuse`'s match into `makeNode$reuse`'s construction (it's passed in).
> 4. **Accumulator uniqueness fixpoint.** The loop accumulator stays unique because it starts unique
>    (step 1) and `set$reuse` returns unique — so no per-iteration copy is needed; result: O(K) once
>    + O(1)/iteration. Bounded **and** fast.
>
> This is genuine Perceus-without-RC. Step 3 (linearity-driven specialization of a recursive
> function group with reuse-token threading) is the large, corruption-prone piece and is the right
> place to resume — gated behind a reuse-correctness + constant-memory + shared-accumulator-still-
> -correct test trio. (The earlier naive deep-copy-in-the-two-pass fallback was tried and segfaults:
> the two-pass copies each arg up to `[C1, C1+K]` then down to `[W, W+K]`, but a `Map.set` iteration
> allocates only O(log K) nodes so `C1 − W ≪ K` and the down-copy clobbers the up-copy mid-walk — so
> a deep-copy fallback would instead need a separate scratch arena.)
>
> ---
>
> Original design below.

Status (original): **Proposed (awaiting sign-off before implementation).**

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
