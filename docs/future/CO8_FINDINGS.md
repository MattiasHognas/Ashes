# CO-8 investigation findings (2026-07-02, session paused — resume from here)

## TL;DR

CO-8's headline is **wrong on both counts**: it is *not* an AVL/balance bug and *not* a stack
overflow. `Ashes.Map` balancing is provably fine (height 15 at 12k sorted keys, 18 at 200k; direct
sorted insertion of 200k keys runs clean). The real bug is a **use-after-free in the TCO back-edge
plain arena reset**: a loop accumulator marked *reset-safe* (`_resetSafeAccumulators`) is assumed
address-stable below the loop watermark, but when the value threaded back into that param slot went
through a nested reuse fold whose **entry deep-copy was NOT elided** (declined seed), the
accumulator is relocated *above* the watermark each iteration — the plain reset then frees live
memory. Crash reproduced on pristine `main` (exit 139).

## Repro on pristine main

`tests-scratch` shape (saved as `docs/future/co8_repro_declined_seed.ash`): an outer TCO loop
threading a map through an inner reuse fold whose seed `w` is **retained** (read after the fold),
which declines the move elision and keeps the entry deep-copy. Keys grow per round
(monotonically increasing — this is what the original report saw), which breaks the accidental
layout symmetry that otherwise masks the corruption. 12 rounds x 1000 new keys on a 1000-key base
SIGSEGVs on current main.

Key facts established:

- Direct sorted `Map.set` folds (12k, 20k, 200k keys), `fromList`, `List.foldLeft`-driven inserts,
  and the nested benchmark shape with full elision (up to **500k keys x 3000 rounds**, RSS 52 MB)
  all pass on main. Balance/rotations fire correctly on ordered input.
- Forcing the elision off (make `IsResultAliasMove` return false — the CO-2c "elision off" A/B
  config) makes the rss-benchmark shape crash at **round 2 for ANY map size (even 5 keys)**.
  The doc's "off" RSS numbers (11.8 MB / 43.1 MB) match crashed runs (`/usr/bin/time -v` reports
  RSS for killed processes).
- gdb backtrace of the crash: `__deepcopy_43` (the synthesized ADT copier) only 2-3 frames deep,
  faulting on a garbage child pointer — corruption, not stack exhaustion. The "~12k+ keys / low
  RSS / stack overflow" framing in CO-8 was a misdiagnosis.
- Mechanism: round 1's fold result is the entry-copy, allocated **above** the outer loop's
  watermark W. The outer back edge takes the **plain reset** path (its acc param is in
  `_resetSafeAccumulators`, added by `LowerReuseSpecializedCall` when it lowers the inline
  `Ashes.Map.set(...)(m)` seed call — `Lowering.cs:5233`). Reset frees the copy; round 2's entry
  deep-copy then reads the dangling tree while bump-allocating its destination over the same
  region (source ~= dest at W) → self-clobber → SIGSEGV. A shape where each round's layout is
  byte-identical (fixed key set) survives by *accident* (dest == source copy is a no-op); growing
  key sets shift the layout and crash.
- Why elision hides it: with the seed admitted as a move (CO-2c), no entry copy happens, the map
  is rewritten in place below W, and the plain-reset assumption actually holds.

## Root cause (one sentence)

`ArgResetSafe` (`Lowering.cs:3538`) justifies the plain arena reset from the accumulator's *param
name* being in `_resetSafeAccumulators`, but never checks that the **back-edge argument
expression** actually preserves the accumulator's address — a nested fold call with a kept
(declined) entry deep-copy returns a *relocated* tree.

## Agreed fix design (worked out in detail, not yet implemented)

Make the plain reset conditional on the back-edge arg being **address-stable**, keeping today's
fast path. All identity checks are collision-proof (no name-keying pitfalls):

1. **`_inPlaceReuseCallExprs : HashSet<Expr>` with `ReferenceEqualityComparer.Instance`** —
   in `LowerReuseSpecializedCall` (pass the `call` Expr node down from `LowerCall` ~line 3452),
   record the call node when `_fullyReusingLabels.Contains(label) &&
   AccumulatorIsFullyPersistent(...)` (same condition as the `_resetSafeAccumulators` marking).
   These calls return their accumulator rewritten in place.

2. **`_accStableFolds : Dictionary<TextSpan, int>`** (fold definition span → param count) — a user
   fold (e.g. `innerFold`) is recorded *address-stable* at the end of its innermost-TCO lambda
   lowering (after the entry-copy splice block, `Lowering.cs` ~3252) iff:
   - its last param A is a spec-path reuse accumulator whose entry copy was **elided**
     (capture a `specElidedAccs` set where `elide` is computed, ~line 3141);
   - every tail leaf of its body is `Var A`, a recorded in-place reuse call with a stable acc arg,
     or a self back edge whose acc-position arg is stable (recursive walk `TailLeavesStable` over
     If/Match/Let with a walk-along shadowed-binder set; Lambda/other = not stable);
   - skip recording when `_inSpecialization || _inParallelSpecialization` (spec clones re-lower
     the same AST/spans).
   Key by `Lookup(tco.SelfName)?.DefinitionSpan` — `Binding.Self` inherits the outer binding's
   span (`Lowering.cs:2947`), so the span the *caller* resolves for `innerFold` is identical, and
   any shadowing binder has a different span by construction. No poisoning needed.

3. **`IsStableAccumulatorExpr(expr, ctx)`** recursive check:
   - `Var v` → ctx-provided acc check. At the **caller/back-edge** site use live scope:
     `Lookup(v.Name) is Binding.Local l && l.Slot == tco.ParamSlots[i]` (exact, shadow-proof).
     In the **recording walk** use name equality + the walk's shadowed set.
   - call node ∈ `_inPlaceReuseCallExprs` → recurse into last arg;
   - head `Var f` resolving (via `Lookup(f.Name)?.DefinitionSpan`) to a recorded stable fold with
     matching arg count → recurse into last arg; self-calls likewise during recording;
   - anything else → false.

4. **`ArgResetSafe(i)`** gains `&& IsStableAccumulatorExpr(collectedArgs[i], ...)` on the
   reset-safe-accumulator branch. When it fails, control falls into the existing branches: MapTree
   has no TCO copy-out kind → "complex heap types — no arena reset" → correct but arena grows per
   iteration (sound fallback; possible follow-up: extend `GetTcoCopyOutKind` with an ADT kind via
   `TrySynthesizeAdtCopier` + the existing two-pass up/down protocol to keep it bounded).

   Note v1 limitation (documented deliberately): a let-bound stable arg
   (`let folded = innerFold(...) in self(...)(folded)`) is NOT traced through the let (sound,
   loses the reset in that style); inline args (the benchmark/test shape) keep the fast path.

Verification matrix for the fix:
- `co8_repro_declined_seed.ash` (growing keys, retained seed): crashes pre-fix → correct output
  post-fix. Add as `tests/` regression with exact `// expect:`.
- rss/nested shape with full elision at 500k x 3000: plain reset must be KEPT (RSS stays ~52 MB
  flat, does not grow with rounds).
- `tests/reuse_map_set_seed_move_elision.ash` and `..._retained_declines.ash` stay green; full
  gate (`scripts/verify.sh` layers) green.
- Optional A/B sanity: with `IsResultAliasMove` forced false, the 50k x 3000 benchmark should now
  run correctly (unbounded arena growth accepted in that diagnostic config).

Also update the CO-8 row in `COMPILER_OPTIMIZATION.md`: real root cause, and note the AVL/stack
overflow hypotheses were disproven.

## Repro files

- `docs/future/co8_repro_declined_seed.ash` — crashes on pristine main (exit 139).
- The forced-off A/B needs a temporary edit: early-`return false` in `IsResultAliasMove`
  (`Lowering.MoveAnalysis.cs:1321`); with it, the rss shape crashes at round 2 at any size.

## Useful debugging bits from this session

- `ASH_DBG_REUSE=1` prints spec/inline decisions; the reset-safe marking and entry-copy elide
  decisions are NOT printed today (temporary prints were added and reverted; re-add at
  `Lowering.cs:5236` and the two `elide` computation sites if needed).
- gdb on a `compile --debug` binary gives symbolized frames (`__deepcopy_NN`, `lambda_NN`).
