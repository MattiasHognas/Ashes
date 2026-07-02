# CO-10 — in-place-reuse generalization for `Ashes.HashMap.set`: investigation notes

Status: **open, not solved.** Eligibility half is easy; the constant-memory half is blocked on a
specializer/closure-lowering subtlety that is not yet explained. This doc is the continuation point —
read it before re-attempting so you don't repeat the dead ends.

## Goal / endpoint

Make a `HashMap.set` fold constant-memory (like the `Map.set` fold already is), so hash-keyed
workloads (1BRC) can use the O(1)-ish `Ashes.HashMap` instead of the string-compare-heavy ordered
`Ashes.Map`. Measured baseline of the gap: the 1BRC `HashMap` variant is **10.1 GB @ 1M rows and 2.6×
slower**, vs **50 MB / 1×** for the `Map` version, *because only `Map.set` gets the in-place reuse.*

Endpoint test: a set-only `HashMap.set` fold over a bounded key set is constant-memory at 1M and 10M
rows (like the `Map.set` equivalent, which is ~1 MB flat).

## Reproduction

Microbench (`hmset.ash`) — 1M `HashMap.set` calls, key = `i mod 1000` (bounded to 1000 keys),
copy-type (`Int`) value:

```
import Ashes.IO
import Ashes.Text
import Ashes.HashMap
let rec loop i n k m =
    if i >= n
    then m
    else loop(i + 1)(n)(k)(Ashes.HashMap.set(Ashes.Text.fromInt(i - i / k * k))(i)(m))
let final = loop(0)(1000000)(1000)(Ashes.HashMap.empty)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.HashMap.size(final)))
```

- `Map.set` equivalent (`Ashes.Map.set(Ashes.String.compare)(key)(i)(m)`): **1 MB, size 1000** — constant.
- `HashMap.set`: **403 MB @ 1M, 4 GB @ 10M** — linear (~400 B/row). Correct output (size 1000) either way.

Useful env vars (already wired in `Lowering.cs`):
- `ASH_DBG_REUSE=1` — prints one `[reuse] spec … fullyReusing=… AllocAdtToSpace=… AllocReusing=…` line
  per specialization, plus `specializable funcs:` / `inlinable funcs:` lists.
- `ASH_DBG_REUSE_IR=1` (with `ASH_DBG_REUSE=1`) — dumps the spec's IR to `/tmp/spec_<label>.txt`.
- During the investigation a temporary `ASH_DBG_DUMP=<label>` dump was added at the top of
  `IsFullyReusing` to print every instruction of a given spec function; re-add it if needed.

## What is already understood (the diagnosis)

### Half 1 — eligibility (EASY, works)

`TryGetNestedRecReturn` (`Lowering.cs` ~462) only recognizes the exact `Map.set` shape:
`fun … -> (let rec go = fun m -> _ in go)` (body is `let rec go … in go`, i.e. `Body: Expr.Var go`).

`HashMap.set` (`lib/Ashes/HashMap.ash`) differs: a leading `let target = hashKey(newKey) in` before
the `let rec`, and the accumulator is a plain outer param applied internally (`… in go(map)`).

Extending the detector to (a) peel leading non-recursive `let`s and (b) accept both the eta
(`… in go`) and non-eta (`… in go(acc)`, `Body: Expr.Call { Func: Expr.Var go, Arg: Expr.Var }`) forms
makes `HashMap.set` **specializable**, and per-node tree reuse then fires:
`AllocReusing=20, AllocAdt=0` — i.e. the AVL nodes are rewritten in place, no per-node bump allocation.
(Note: the AST application node is `Expr.Call(Func, Arg)`, not `Expr.App`.)

`AccumulatorIsFullyPersistent` (`Lowering.cs` ~5382) is hardcoded to `MapTree`; the *materialization*
it guards (`LowerConstructorApplication`, `Lowering.Symbols.cs` ~661–705) is generic and already handles
`HashMapTree`'s `Str` key + copy/`V` value correctly. A clean structural rewrite of
`AccumulatorIsFullyPersistent` (enumerate `named.Symbol.Constructors[*].ParameterTypes`, substitute type
params via `InstantiateConstructorParameterType`, accept a field iff it is the same recursive named type
or `IsReuseMaterializableFieldType`) subsumes the `MapTree` case and generalizes it — but this was **not
the blocker** (see below), so it was not needed to reach the wall.

### Half 2 — constant memory (BLOCKED)

Even specializable, `HashMap.set`'s fold stays **linear**. `ASH_DBG_REUSE=1` shows:

```
spec Ashes_HashMap_set -> …, fullyReusing=False, AllocAdtToSpace=18 AllocAdt=0 AllocReusing=20
spec Ashes_Map_set     -> …, fullyReusing=True,  AllocAdtToSpace=18 AllocAdt=0 AllocReusing=20
```

**Identical alloc counts; only `IsFullyReusing` (`Lowering.cs` ~5311) differs.** `fullyReusing` gates
whether the loop back-edge may reclaim the main arena (constant memory). `IsFullyReusing=False` ⇒ arena
not reset ⇒ per-row scratch accumulates ⇒ linear.

Why `IsFullyReusing` rejects `HashMap.set` but not `Map.set` — traced to closure compilation:

- In **`Map.set`'s** spec, the recursive `go` **is the spec function itself** (one label). Self-calls
  `go(left)`/`go(right)` resolve via `Binding.Self` to a fresh `MakeClosure(<spec label>) + CallClosure`
  at each site — which `IsFullyReusing` accepts (`MakeClosure` feeding only `CallClosure`). There is **no
  stored `go` closure** (verified: no `MakeClosure` target is a `StoreLocal` source in `Map.set`'s spec).
- In **`HashMap.set`'s** spec, `go` is a **separate nested function** (its own label, e.g. `lambda_40`).
  It is materialized once as `MakeClosure` and `StoreLocal`'d into the `let rec` slot
  (`LowerLetRec` always emits `StoreLocal(slot, valTemp)` at ~2623). `IsFullyReusing` rejects a
  `MakeClosure` whose reader is `StoreLocal` (a possible escape). That single rejection is the whole
  difference.

## Dead ends (do NOT repeat)

1. **Leading-`let` hypothesis — DISPROVEN.** Removing `let target` (inlining `hashKey(newKey)` at the use
   sites) still produced a stored `go` closure (`reject MakeClosure … StoreLocal`). So the leading `let`
   is not the trigger.
2. **Eta vs non-eta form — DISPROVEN.** Both `… in go` (eta) and `… in go(map)` (non-eta) produce the
   stored closure. The non-eta form is actually *worse conceptually* (it does not eta-unify `go` with the
   spec), but empirically both are `fullyReusing=False` for the same reason.
3. **Relaxing `IsFullyReusing`'s `Alloc` check** to allow a closure-env `Alloc` populated by
   `StoreMemOffset` (BasePtr == alloc target) and consumed by `MakeClosure`: this *is* sound and removes
   one spurious rejection, but the **`MakeClosure → StoreLocal`** rejection remains, so it does not reach
   constant memory on its own. (Kept as a candidate partial fix, not sufficient alone.)

## The open question (start here)

**Why does `Map.set`'s recursive `go` unify with the spec label (fresh `Binding.Self` closures) while
`HashMap.set`'s `go` becomes a separate `StoreLocal`'d nested function?** Both are structurally
`fun … -> (… let rec go = <lambda> in go[(acc)])`. Both `go`s capture (`EnvSizeBytes` 24 vs 32). Neither
is empty-env. The label-unification path was not located despite tracing:
- `_specializingReuseLabel` is set at `Lowering.cs` ~2946 when the linear-param lambda is lowered, but is
  only *read* post-hoc at ~5269 (to pick the `recursiveFunc` to check) — it is not used to redirect
  self-calls during lowering.
- The spec is lowered by `LowerLambdaCore(spec.Lambda, selfName: name, forcedLabel: label)` at ~5275;
  `forcedLabel` lands on the outermost lambda (`~2898 label = forcedLabel ?? …`). How/whether that label
  reaches the inner `go` for `Map.set` (making `go == spec`) is the missing link.
- `Binding.Self` is created at ~2978 for `selfName` inside `LowerLambdaCore`; `LowerLetRec` binds the rec
  name to `Binding.Local(slot)` (~2528) and self-recursion inside the rec lambda goes through
  `Binding.Self`. Find why `Map.set`'s `go` self-calls target the *spec* label, not a nested `go` label.

## Candidate fix directions (once the above is understood)

- **A — make `HashMap.set`'s `go` unify with the spec label**, exactly as `Map.set`'s does. Likely the
  cleanest: it makes the stored closure disappear (no `StoreLocal`), so `IsFullyReusing` passes with no
  relaxation. Requires understanding the label-unification path above.
- **B — teach `IsFullyReusing` to accept a stored-but-non-escaping recursive `let rec` closure.** The
  `go` closure is `StoreLocal`'d into its own `let rec` slot, read only by `LoadLocal → CallClosure`
  (self-recursion) and possibly the return. Prove it does not escape into the accumulator and allow it.
  Higher risk: a returned closure genuinely escapes the spec frame; soundness must be argued carefully
  and **scale-tested with a growing (not cycling) key set** (the CO-8 UAF class only shows when the tree
  layout shifts each iteration).

## Hard requirement for any attempt

This is the compiler's most use-after-free-prone code (see CO-8 / CO-9). Before shipping, verify:
- Correctness of a `HashMap.set` fold with a **growing** key set at ≥300k rows (not just cycling keys)
  — this is what exposed the CO-8 relocation UAF.
- Byte-identical output vs the `Map` version on the same data.
- The full gate (C# unit, LSP, e2e) plus the 1BRC `HashMap` variant dropping from ~10 GB to tens of MB.

## Code map (line numbers approximate, `src/Ashes.Semantics/`)

- `Lowering.cs` ~462 `TryGetNestedRecReturn` — eligibility detector (eta shape only today).
- `Lowering.cs` ~423–451 — `_specializableFunctions` registration (structural).
- `Lowering.cs` ~2514 `LowerLetRec` — ~2623 emits `StoreLocal(slot, closure)` (the flagged store).
- `Lowering.cs` ~2828 `LowerLambdaCore` — ~2898 label assignment, ~2978 `Binding.Self`.
- `Lowering.cs` ~5229 `GetOrCreateReuseSpecialization` — ~5275 lowers the spec; ~5277 `IsFullyReusing`.
- `Lowering.cs` ~5311 `IsFullyReusing` — the gate; rejects `MakeClosure → StoreLocal`.
- `Lowering.cs` ~5382 `AccumulatorIsFullyPersistent` — `MapTree`-hardcoded (generalizable, not the blocker).
- `Lowering.Symbols.cs` ~661–705 — generic fresh-heap-field materialization (already handles `HashMapTree`).
- `lib/Ashes/HashMap.ash` ~130 `set` — the target function.
