# Deterministic Resource Safety — Findings & Plan

Status: **Phase 0 implemented (2026-06-30); Phases 1–2 designed, pending the shared move/linearity analysis.**

> **Progress:** Phase 0 (the `Process` drop) is **done & verified** — see §4. Phases 1 (recursive
> drop for resource-bearing aggregates, Gap A) and 2 (move/escape analysis, Gap B) both hinge on a
> flow-sensitive ownership/move analysis — the **same engine [REUSE_ANALYSIS.md](REUSE_ANALYSIS.md)
> (#2 in-place reuse) needs**. That engine is the gating, correctness-critical next step: a wrong
> inference here is a double-close (Gap A) or an incorrectly-rejected valid program (Gap B), so it
> wants the same careful, test-gated build the rest of this plan describes — it was deliberately not
> rushed. Build it once; it serves both resource safety and in-place reuse.

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
