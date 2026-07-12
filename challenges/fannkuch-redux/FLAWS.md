# fannkuch-redux — flaws found (all fixed)

fannkuch-redux is *fully expressible purely*, yet implementing the faithful factorial-order
enumeration surfaced **three distinct compiler bugs** — it compiled and ran only for `N <= 2` and
segfaulted at `N >= 3`. All three are now fixed and the benchmark runs correctly; each reduced
reproducer is below with its fix.

## Bug 1 — two threaded list accumulators dropped an early ADT return (FIXED)

A self-recursive function threading **two** `List` accumulators and returning an ADT *early* took the
wrong branch — the early (non-tail) return was dropped and the base case returned:

```ash
let recursive g r n perm count =
    if r == n then Base
    else
        let cr = getAt(r)(count) - 1
        in if cr > 0 then Hit(r) else g(r + 1)(n)(r :: perm)(setAt(r)(cr)(count))
// g(1)(3)([2,1,3])([0,1,3]) used to return Base; correct is Hit(2).
```

**Root cause:** the TCO back-edge arena reset used a single-cell shallow copy-out for list
accumulators, which preserves only a list's *top* cons cell. That is sound only for `head :: <loop
param>` (one fresh cell). A list rebuilt by `setAt` (multiple fresh interior cells) had those cells
reclaimed by the reset, so the threaded `count` was corrupted and `cr` never exceeded 0. **Fix:** the
reset is disqualified for any list accumulator that is not a single fresh cons; such loops no longer
reclaim (rather than reclaiming unsoundly). Regression test:
`tests/tco_multi_fresh_list_accumulator.ash`.

## Bug 2 — spurious ASH014 for a non-recursive helper calling a recursive one (FIXED)

A **non-recursive** `let f x = … g …` whose body calls a recursive helper `g`, when `f` is itself
called from a later recursive function, was rejected with `ASH014 … not yet declared` (mis-located).
**Fix:** the backward-reference reconstruction no longer requires the specialization path, so a helper
already lowered earlier resolves as the valid backward reference it is. `rotateFirst` and `flip` are
now plain `let`. Regression test: `tests/regression_ash014_nonrecursive_helper_calls_recursive.ash`.

## Bug 3 — use-after-reset of a threaded accumulator across the TCO back-edge (FIXED)

The `loop` function threads a `State(perm, count)` value (two lists) as a tail-recursive accumulator;
at `N >= 3` the process segfaulted. **Same root cause as Bug 1** — the unsound shallow copy-out
reclaimed still-referenced interior cells. The Bug 1 fix (disqualifying the reset for non-single-fresh-
cons accumulators) resolves the crash too.

## Residual (memory-model, still open)

Fixing the crash means these loops no longer reclaim their per-iteration garbage: a *growing* pointer-
bearing accumulator threaded through the loop stays resident, so RSS grows with `N!`. That is the
ownership / in-place-reuse milestone (FLAWS #2), not a point fix — it is why `N=12` is out of reach.
