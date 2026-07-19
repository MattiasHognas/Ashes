# Uniqueness Typing — Design (Increment 5)

Status: **design, pre-implementation.** This is the design brief for turning ownership uniqueness
from a heuristic whole-program optimizer analysis into a principled, type-directed property. It is on
the `ownership-uniqueness-typing` branch for review before any compiler code is written.

## 1. The reframing (read this first)

The roadmap doc ([OWNERSHIP.md](OWNERSHIP.md)) described this increment partly as "the compiler
*rejects* unsafe programs instead of silently miscompiling." That framing is correct for **resources**
— and Increments 1–3 already delivered it (use-after-move `ASH008`, second-order escape, recursive
resource-bearing ADT drop). Resources are genuinely **affine**: a file handle or socket has identity
and side effects, so using one after its ownership moved is an error.

For **ordinary values it is the wrong framing.** Ashes is pure and immutable, so *sharing a value is
always safe* — there is no mutation that aliasing could make unsound. A program that uses the same
list twice is correct; the compiler must never reject it. So for ordinary values, uniqueness is **not
a safety property and there is nothing to reject.** It is an **optimization-soundness property**: it
tells the compiler *when in-place reuse and in-place reclaim are sound*, so it can do them as a
*guarantee* instead of a best-effort guess.

This is the essential difference from Rust. Rust needs uniqueness because it has mutation; aliasing +
mutation is unsound, so it rejects. Ashes has no mutation, so:

- **Resources** → affine, rejection discipline (done, Increments 1–3).
- **Ordinary values** → uniqueness is an *inferred guarantee that enables optimization*, with **no new
  user-facing rejections and no new syntax.**

Everything below is about the second bullet.

## 2. What exists today

`Lowering.MoveAnalysis.cs` (~2048 lines) is a **whole-program** analysis over the single stitched,
desugared program expression. It produces exactly one boolean payoff: *may the entry deep-copy of a
fold accumulator be elided?* — read at `Lowering.cs:2858` / `:2908`. It is built from two fixpoints:

- **GFP `IsParamMoveSafe(func, param)`** — "the value bound to parameter `param` of function `func` is
  uniquely owned at every external call site." This is the closest thing to a uniqueness bit, but it is
  keyed on `(function, parameter)`, not on values, and cycles resolve to `false`.
- **LFP result-reach** (`_maResultReach`) — per function, an over-approximation of which of the
  function's own parameters its *result* may alias (multiplicity capped at 2 → poison) plus a poison
  flag for "escapes / not confined to params." A function whose result reaches `{}` unpoisoned is
  *result-fresh*.

The default is always **copy-stays**: anything unproven keeps the copy, so the analysis can only leak,
never corrupt.

**Conservatism costs (real programs left unoptimized):** any function used as a first-class value
(passed to a combinator) *escapes* and becomes unanalyzable; fresh values built from a bound-but-unique
variable are rejected; `RecordUpdate` / `Await` / higher-order shapes poison. The root cause is that
the analysis needs **full whole-program visibility** of every call site, which higher-order code
breaks.

## 3. Proposed architecture: inferred per-function typed summaries

Re-express the two whole-program fixpoints as **inferred, per-function summaries attached to the
function's type** — computed per function, composed at call sites:

1. **Uniqueness input-contract** (per parameter): "reuse of this parameter is sound only if the
   argument is unique here." Re-expresses the GFP, but as a *local* contract a caller discharges,
   rather than a global scan of all call sites.
2. **Result-reach output-effect** (on the return): "the result is fresh, or aliases parameters
   {i, j, …}." Re-expresses the LFP as a summary carried *on the function's type*.

Both are **inferred** — there is **no user-written syntax**. They are an internal type-level attribute
(the natural anchors, per the code map: the parameter position in the signature for the input contract,
and a result-reach effect on the return type). Composing summaries at call boundaries is what removes
the whole-program full-visibility requirement: a higher-order function can be analyzed from its
parameters' summaries instead of needing every call site visible.

### Payoffs

- **Guaranteed reuse** instead of heuristic — the "compile-time-verified, no-GC" claim (ground rule #6)
  becomes literally true where the property holds.
- **The to-space reclaim (Increment 4, folded here):** once uniqueness proves an overwritten heap-leaf
  value is dead and unaliased on the reuse path, reclaim the old to-space blob instead of leaking it
  (measured leak: 1.5 MB at 100k iterations → 30 MB at 2M).
- **Less conservatism:** composable summaries analyze higher-order and escaping code the whole-program
  pass gives up on.
- **Retire the heuristic (Increment 7):** the entry deep-copy becomes a lowering of a proven fact;
  delete the conservative fallbacks.

### User-visible impact

None. No new syntax, no new diagnostics for ordinary values, no rejected programs that compile today.
Purely a stronger, sound basis for optimizations that already exist plus the reclaim. Resource
diagnostics (`ASH006`/`007`/`008`) are unchanged.

## 4. Sub-step plan (each lands independently, green)

- **S1 — Make the property first-class (foundation, behavior-preserving).** Lift the two fixpoints
  into an explicit per-function summary type and attach it to function signatures, with the existing
  whole-program computation feeding it. No behavior change; adds direct unit tests for the summaries.
  De-risks everything after.
- **S2 — Compose summaries across boundaries.** Analyze functions from their parameters' summaries so
  escaping / higher-order folds stop being unconditionally unanalyzable. Measured by newly-elided
  copies on programs that keep the copy today.
- **S3 — To-space reclaim (Increment 4).** Use proven uniqueness to reclaim the dead overwritten
  heap-leaf blob on the reuse path. Verified by the RSS measurement going flat.
- **S4 — Retire the heuristic (Increment 7).** Where the typed property proves uniqueness, drop the
  defensive entry deep-copy and delete the now-dead conservative fallbacks.

## 5. Open decisions (for review before implementing)

1. **Framing** — confirm: for ordinary values this is an inferred *guarantee that enables
   optimization*, **not** a rejection discipline (no new diagnostics, no rejected programs). Resources
   keep their affine rejection from Increments 1–3.
2. **Surface** — confirm: **fully inferred, no user syntax** (matches the "users never write
   move/borrow/drop" invariant), even though inferred per-function *signatures* are introduced
   internally.
3. **First sub-step** — recommend **S1** (make the property first-class, behavior-preserving) as the
   foundation before touching behavior or the runtime.
