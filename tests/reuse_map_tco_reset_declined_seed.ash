// expect: 7
// CO-8 regression: a TCO back-edge plain arena reset must not free a RELOCATED accumulator.
//
// `rounds` is an outer TCO loop threading a map `m`. Its `m` gets marked reset-safe because the seed
// `w = Ashes.Collection.Map.set(cmp)(0)(0)(m)` is a fully-reusing (in-place) specialization. But `w` is RETAINED
// (read via `getv(w)(0)`), so the move analysis DECLINES to elide the inner fold's entry deep-copy:
// `folded = innerFold(...)(w)` runs on a COPY of `w`, allocated ABOVE the loop watermark. Threading
// `folded` back into `m` and then taking the plain reset (which reclaims everything above the
// watermark) freed the live tree — a use-after-free that SIGSEGV'd on the next round's deep-copy.
// Keys grow per round (r*1000..r*1000+999), so the per-round layout shifts and the corruption is not
// masked by an accidental byte-identical reuse.
//
// The fix makes the plain reset additionally require the back-edge argument to be provably
// address-stable. Here the argument is the let-bound `folded` (not the in-place-rewritten `m`), so the
// reset is declined and the arena simply grows for the loop's duration (sound). Fully-elided nested
// reuse folds — where the argument IS the accumulator rewritten in place — keep the fast reset.
//
// Key 1 is set to 7 in `base` and never overwritten; every round adds only 0 to the accumulator
// (getv(w)(0) == 0), so the result is 0 + getv(m)(1) == 7.
import Ashes.Collection.Map
import Ashes.IO
import Ashes.Text
let cmp a b =
    if a == b
    then 0
    else
        if a <= b
        then -1
        else 1

let getv m k =
    match Ashes.Collection.Map.get(cmp)(k)(m) with
        | None -> -1
        | Some(v) -> v

let recursive innerFold i lim m =
    if i > lim
    then m
    else innerFold(i + 1)(lim)(Ashes.Collection.Map.set(cmp)(i)(i * 7)(m))

let recursive rounds r acc m =
    if r <= 0
    then acc + getv(m)(1)
    else
        let w = Ashes.Collection.Map.set(cmp)(0)(0)(m)
        in
            let folded = innerFold(r * 1000)(r * 1000 + 999)(w)
            in rounds(r - 1)(acc + getv(w)(0))(folded)

let base = innerFold(0)(999)(Ashes.Collection.Map.empty)

let result = rounds(12)(0)(base)

Ashes.IO.print(Ashes.Text.fromInt(result))
