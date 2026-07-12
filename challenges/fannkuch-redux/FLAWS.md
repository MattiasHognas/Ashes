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

## Memory — now constant (FIXED)

Fixing the crash initially left these loops unable to reclaim per-iteration garbage (the reset was
disqualified for the pointer-bearing `State`), so RSS grew with `N!` (4.6 GB at N=10). That is now
fixed: `State(perm, count)` is a **fixed-shape, non-recursive** ADT, so it is carried across the reset
by a recursive **deep copy** — a self-contained clone whose list fields are fully copied, which breaks
any tail-sharing with the previous accumulator. Being self-contained, it resets to the fixed loop-entry
watermark, so the reset fires and reclaims every transient. Resident set is a constant 0.25 MB at every
N; N≥11 is now reachable, bounded only by `N!` enumeration time.

Self-*recursive* ADTs (trees such as `MapTree`) are deliberately excluded — a full per-iteration deep
copy of an unbounded tree would be O(size)/iteration, and those shapes are owned by the in-place reuse
specialization. A growing tree accumulator outside that specialization is the remaining memory-model
work.
