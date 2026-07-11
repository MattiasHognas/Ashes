# fannkuch-redux — flaws found

fannkuch-redux is *fully expressible purely* (per the README), yet implementing the faithful
factorial-order enumeration surfaced **three distinct compiler bugs**. The benchmark cannot run to
completion until they are fixed: `fannkuch-redux.ash` compiles and produces correct output for
`N <= 2`, then **segfaults for `N >= 3`**. Each bug is reduced to a small reproducer below.

## Bug 1 — two independently-threaded list accumulators drop an early ADT return

A self-recursive function that threads **two** `List` accumulators and returns an ADT *early* (a
non-tail return) takes the wrong branch: the early return is dropped and the base case is returned
instead. With **one** threaded list it is correct; packing the two lists into a single value is the
workaround used in `fannkuch-redux.ash`.

```ash
import Ashes.IO as io
import Ashes.Text as text
type R = | Base | Hit(Int)
let recursive getAt i xs = match xs with | [] -> 0 | h :: t -> if i == 0 then h else getAt(i - 1)(t)
let recursive setAt i v xs = match xs with | [] -> [] | h :: t -> if i == 0 then v :: t else h :: setAt(i - 1)(v)(t)
let recursive g r n perm count =
    if r == n
    then Base
    else
        let perm2 = r :: perm
        in let cr = getAt(r)(count) - 1
        in let count2 = setAt(r)(cr)(count)
        in if cr > 0 then Hit(r) else g(r + 1)(n)(perm2)(count2)
// g(1)(3)([2,1,3])([0,1,3]) should reach r=2 (cr=2>0) and return Hit(2); instead returns Base.
io.print(match g(1)(3)([2, 1, 3])([0, 1, 3]) with | Base -> "BASE (bug)\n" | Hit(r) -> "HIT " + text.fromInt(r) + "\n")
```

Prints `BASE (bug)`; expected `HIT 2`. Removing either the second threaded list, the early return,
or reading the threaded list via `getAt` all make it correct — so the trigger is the combination.

## Bug 2 — spurious ASH014 for a non-recursive helper that calls a recursive helper

A **non-recursive** `let f x = … g …` whose body calls a recursive helper `g`, when `f` is itself
called from a later recursive function, is rejected with `ASH014 Binding 'g' is not yet declared at
this point` (the error is even mis-located at an unrelated later declaration). Marking `f`
`let recursive` (though it does not self-recurse) works around it — this is why `rotateFirst` and
`flip` are `let recursive` in `fannkuch-redux.ash`.

## Bug 3 — use-after-reset of a threaded accumulator across the TCO back-edge (segfault)

The `loop` function threads a `State(perm, count)` value (a constructor holding two lists) as a
tail-recursive accumulator. At `N >= 3` — i.e. once the enumeration takes more than a couple of
back-edges — the process **segfaults**. The pattern matches the known back-edge address-stability
class (`challenges/1brc` FLAWS #2 / the TCO plain-reset UAF on relocated accumulators): the arena
reset on the loop back-edge reclaims the `State` (and the lists it points at) that the next
iteration still reads. Constant-memory reuse specialization is fine for flat integer accumulators
but not for a pointer-bearing accumulator rebuilt each iteration here.

## Net

fannkuch-redux is not a missing-feature blocker — it is a *correctness* blocker: the pure
enumeration is expressible, but three codegen bugs (two miscompiles + one spurious diagnostic) stop
it running. The benchmark table in `README.md` is deferred until Bug 3 (the crash) is fixed.
