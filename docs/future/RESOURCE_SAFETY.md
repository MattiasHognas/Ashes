# Deterministic Resource Safety — Findings & Plan

Status: **All known gaps (A, B, C, D) fixed & verified, including deterministic close of escaped resources (2026-06-30).**

> **Deterministic close of escaped resources — DONE.** A closure now carries a dropper at
> `closure+24` (32-byte closure layout). When a resource is captured by a closure that escapes its
> defining scope (so the resource is solely the closure's — `SkipDropsForResourcesEscapingViaResult`
> only fires there), the scope moves the resource into the closure and attaches a synthesized dropper
> (`SynthesizeClosureResourceDropper` + `LoadFuncAddr`). When the closure is dropped — at its scope
> exit or the TCO back-edge — `EmitDrop "Function"` invokes the dropper to close the resource. So an
> escaped resource is closed deterministically at the closure's death rather than leaking to program
> exit. Aggregates were already deterministic (recursive Drop fires when the resource-bearing binding
> is dropped). Drop elision keeps `Function` drops (they may carry a dropper). Verified: an escaping
> closure over a handle, recreated each loop iteration, stays fd-bounded under `ulimit -n 64`
> (`closleak`); e2e `resource_closure_deterministic_close.ash`.

> **Gap A (resource nested in an aggregate) is now FIXED too**, via an affine ownership model:
> - **Recursive `Drop` for resource-bearing aggregates** (`EmitResourceBearingDrop` /
>   `EmitAdtResourceDrop` / `EmitListResourceDrop` in Lowering.Ownership.cs, driven by
>   `IsResourceBearing`): a still-owned `Result(_, FileHandle)`, `Some(Socket)`, tuple/list of
>   resources, etc. is walked and its nested resources closed (also at the TCO back-edge).
> - **Move-on-destructure** (`LowerMatch`): matching a resource-bearing binding consumes it (nested
>   resource moves to the arm's pattern bindings), so its own recursive Drop is skipped — no
>   double-close.
> - **Move-on-construction** (`MarkResourceArgMoved` at constructor/tuple/cons sites): storing a
>   resource into an aggregate moves ownership into it, so a returned `Some(handle)` keeps the
>   handle open for its consumer (aggregate analog of Gap B) and an aggregate dropped later isn't a
>   double-close. Tests: `resource_aggregate_escape.ash`; verified `let _r = open()` non-destructured
>   stays bounded under `ulimit -n 64`, and `let r = open() in match r` is a single close.
>
> Remaining refinement: escaped resources (Gap B closures, aggregates returned past their scope) are
> released at **program exit** rather than deterministically when their carrier dies — full
> deterministic close needs carrier-drops-its-captured-resource lifetime tracking. Everything below
> is the original audit + plan.

> **Progress (the common/impactful patterns now work):**
> - **Gap C (`Process` undropped) — FIXED** (Phase 0; `EmitProcessDrop`).
> - **Gap D (resource bound in a TCO tail-call arm leaks) — FIXED.** The most impactful gap — the
>   loop-over-files/connections pattern. Back-edge now closes iteration-local resources
>   (`EmitTcoBackEdgeResourceDrops`); a resource passed as a self-call arg moves to the next
>   iteration and is skipped. Test `tests/resource_tco_loop_cleanup.ash`.
> - **Gap B (resource captured by an escaping closure → use-after-close) — FIXED.** The owning scope
>   no longer closes a resource its result closure captures (`SkipDropsForResourcesEscapingViaResult`
>   + `_closureResourceCaptures`); correct results, resource released at program exit. Test
>   `tests/resource_capture_escape.ash`.
> - **Gap A (resource nested in a non-destructured aggregate, e.g. `let _r = File.open(p)`) —
>   REMAINING.** Rare (binding a resource result and ignoring it / collections of resources). Needs
>   recursive `Drop` for resource-bearing aggregates **and** move-on-store/destructure to avoid a
>   double-close (closing a reused fd is harmful), so it wants the careful flow-sensitive ownership
>   analysis the rest of this plan describes — the same engine [REUSE_ANALYSIS.md](REUSE_ANALYSIS.md)
>   (#2) needs. Deliberately not rushed.

Ashes Ground Rule 6 promises *"all resource and memory management is deterministic and
**compile-time verified**"* with *"no user-visible `Drop`"* (Ground Rule 4). This doc audits how
well the current implementation delivers that for the resources we now expose through syntax —
**file handles, TCP/TLS sockets, and processes** — and proposes a plan to close the gaps without
breaking the Ground Rules (pure, immutable, no GC, no user-visible Drop).

## 1. What exists today

There *is* a deterministic-destruction mechanism (`src/Ashes.Semantics/Lowering.Ownership.cs`):

- Resource types are a fixed set: `Socket`, `TlsSocket`, `Process`, `FileHandle`
  (`BuiltinRegistry.ResourceTypeNames`).
- When a value of resource type is bound **directly to a name** (a `let` binding or a `match`
  pattern variable), it is registered with `TrackOwnedValue`. At scope exit `PopOwnershipScope`
  → `EmitDropsForCurrentScope` emits an `IrInst.Drop` for every still-alive owned value.
- An explicit `close`/`waitForExit` marks the value dropped (`TryMarkDropped`), so the scope-exit
  drop doesn't double-fire; a second close is flagged (`ASH` use-after-drop) and double-drop is
  detected.
- Backend `EmitDrop` (`LlvmCodegenBuiltins.cs`) closes the OS resource: `FileHandle` → close(2),
  `Socket` → TCP close, `TlsSocket` → TLS close.

This works for the **idiomatic, directly-bound, scope-local** case. Verified: opening a file 5000×
in a recursive loop with **no** explicit `close` does **not** exhaust fds — the arm-scoped handle is
auto-closed each iteration.

## 2. Where it breaks (all reproduced)

### Gap A — resources nested in a non-resource aggregate leak
Tracking only fires for a name whose *own* type is a resource type. A resource nested inside an
ADT/tuple/list/closure is invisible. The worst part: **`File.open` itself returns
`Result(Str, FileHandle)`** — the handle is *always* delivered inside a `Result`.

```ash
let _r = Ashes.File.open("data.txt")   -- bound as Result(Str, FileHandle), never destructured
in loop(n - 1)                          -- the FileHandle inside _r is never tracked → fd leaks
```
Reproduced: with `ulimit -n 64`, this exhausts fds after ~31 iterations. `EmitDrop` also never
recurses into an aggregate, so even a *tracked* `Result`-typed binding would not close the handle
inside it.

### Gap B — a resource that escapes its scope is closed too early (silent use-after-close)
`EmitDropsForCurrentScope` drops every value still alive in the scope, including one that has been
**captured by a closure** or **returned**. Ownership "escaping" is not modelled, so the drop fires
while a live alias still exists.

```ash
let reader =
    match Ashes.File.open("data.txt") with
        | Error(_e) -> (fun (x) -> "no-file")
        | Ok(handle) -> (fun (x) -> ... Ashes.File.readChunk(handle)(5) ...)  -- captures handle
in reader(0)     -- handle was auto-closed at the Ok-arm scope exit → reads a closed fd
```
Reproduced: returns `read-err` instead of the file's first 5 bytes. **No diagnostic** —
`CheckUseAfterDrop` only inspects direct `Expr.Var` uses in the same scope, not captures/returns.
This is an unsound use-after-close.

### Gap D — a resource bound in a TCO tail-call arm leaks (found & fixed 2026-06-30)
The per-arm `Drop` is emitted *after* the tail-call back-edge jump, so for a loop like
`let rec loop n = … match File.open(p) with Ok(h) -> loop(n-1)` the close becomes dead code and
the fd leaks every iteration (5000 opens exhaust a low `ulimit -n`). This is the common
loop-over-files/connections pattern — the most impactful resource gap. **Fixed:** the back-edge now
closes iteration-local resources before resetting the arena and jumping (a resource *passed* as a
self-call argument moves to the next iteration and is skipped). See `EmitTcoBackEdgeResourceDrops`.

### Gap C — `Process` has no cleanup at all
`EmitDrop` handles `FileHandle`/`Socket`/`TlsSocket` but **not `Process`** — it falls through to the
no-op default. A spawned process that is never `waitForExit`'d is never reaped (zombie) and its
pipe fds are not deterministically closed by the drop path. (Empirically pipe fds survived a
3000-spawn stress run, so spawn closes some ends eagerly, but the reaping/close contract is not
expressed through `Drop` and is therefore not guaranteed.)

### Summary
The current model is **runtime best-effort scope-drop**, not the **compile-time-verified** guarantee
Ground Rule 6 states. It is sound only for directly-bound, non-escaping, non-nested resources.

## 3. Root cause

Three missing pieces, all the same shape — *the compiler does not track resource ownership through
the places a value can travel*:

1. **Through aggregates** (construction nests a resource; destructuring/`Drop` must move/recurse).
2. **Through escape** (capture, return, store ⇒ ownership *moves out*, so the origin scope must
   **not** drop it, and the new owner must).
3. **Process** simply isn't wired into `Drop`.

(1) and (2) are exactly an **affine ownership / linearity** discipline — and that is the *same*
flow analysis [REUSE_ANALYSIS.md](REUSE_ANALYSIS.md) (#2 in-place reuse) needs to prove a value is
"used exactly once." Build the analysis once, serve both.

## 4. Plan (fits the Ground Rules: pure, immutable, no GC, no user-visible Drop)

Make resource ownership a **static, flow-sensitive, affine** property, verified at compile time;
keep cleanup fully automatic (no surface `Drop`).

**Phase 0 — close the trivial hole. ✅ DONE (2026-06-30).** `EmitDrop` now has a `Process` case
(`EmitProcessDrop`): closes the three pipe fds and reaps the child (non-blocking `waitpid(WNOHANG)`
on Linux so a still-running child never stalls the drop; `CloseHandle` on Windows). `Process` was
already tracked as a resource type — only the backend cleanup was missing. Test:
`tests/process_drop_releases_fds.ash` (drop-path smoke at scale); fd release verified directly under
`ulimit -n 64`. Tree green.

**Phase 1 — ownership-aware `Drop` for aggregates (fix Gap A).**
- Mark any aggregate type (ADT/tuple/list/closure-env) that **transitively contains a resource type**
  as *resource-bearing*. Compute this once per type, recursively (guard recursive ADTs).
- `EmitDrop` recurses: dropping a resource-bearing aggregate walks its fields/elements/captures and
  drops each contained resource (type-directed, like the existing `EmitDeepCopy` walker — reuse that
  structure). So an un-destructured `Result(Str, FileHandle)` closes its handle at scope exit.
- Destructuring a resource-bearing aggregate **transfers** ownership to the bound sub-patterns (the
  container is now empty — don't double-drop). This generalises today's `TrackOwnedBindingsInPattern`.

**Phase 2 — ownership transfer / move analysis (fix Gap B).** A flow-sensitive pass over the IR (in
`Lowering.Ownership.cs`) that, for each resource (or resource-bearing) value, tracks a single owner
and marks ownership **moved** when the value is: captured by a closure that escapes, returned,
stored into an aggregate that escapes, or passed to a function that takes ownership.
- A scope only auto-drops values it **still owns** at exit (not moved-out ones). The new owner (the
  escaping closure / the caller / the aggregate) carries the drop obligation.
- Generalise `CheckUseAfterDrop` into **use-after-move**: using a resource after its ownership moved
  is a compile error, covering captures and returns, not just same-scope `Var` uses.
- Reject **ambiguous ownership** (e.g. a resource moved on one `match` arm but not the sibling, or a
  resource that would be dropped twice) with a clear diagnostic — this is the Ground-Rule-6
  "compile-time verified" part. Conservative: when ownership can't be proven single, reject rather
  than silently leak/double-close.

**Phase 3 — spec + diagnostics + tests.**
- `LANGUAGE_SPEC.md`: state resource values are **affine** (used at most once; cleanup is automatic
  and deterministic at the unique point ownership ends), and that nesting/capture/return transfer
  ownership.
- New `DIAGNOSTICS.md` codes: use-after-move, ambiguous/conditional ownership, (optionally) an
  unused-resource lint. Keep "no user-visible `Drop`": users never write cleanup; the compiler proves
  and inserts it.
- Tests: the three reproductions above must become (a) correct-and-leak-free where the program is
  sound (nested-Result closes; escaped-capture stays open until the *new* owner's scope ends), and
  (b) a precise diagnostic where the program is genuinely unsound.

### Why this is the right shape for Ashes
- **Deterministic, no GC / no refcount:** affine ownership means each resource has exactly one owner
  and is closed at exactly one statically-known program point.
- **Purity preserved:** ownership/drop is an internal property; no observable mutation, no surface
  syntax.
- **Reuses in-flight work:** the move/linearity analysis is the same engine #2 needs; the recursive
  `Drop` walker mirrors the existing `EmitDeepCopy` type-directed walker.

### Risks / open points
- Flow-sensitivity across `match`/recursion must be conservative but not so strict it rejects the
  idiomatic match-arm pattern that works today (keep that a zero-friction path).
- Ownership through curried/partial application and through lists of resources needs care (a list of
  sockets ⇒ drop each).
- Interaction with the (future) parallel `both`: a resource captured by a worker thunk must have its
  ownership/drop semantics defined (likely: resources may not cross a `both` boundary — pure CPU work
  only — enforced statically).
