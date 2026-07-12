// expect: 100 999
// CO-2c soundness discriminator: a Map.set RESULT that is RETAINED must NOT be moved into a reuse fold.
//
// `w = Ashes.Map.set(0)(100)(empty)` is a Map.set result — an admissible move seed IN ISOLATION under
// CO-2c. But here it is RETAINED: `keep = w` reads it after `bump(3)(w)`. Because `w` is used twice it
// is not move-linear, so the analysis DECLINES to elide `bump`'s entry deep-copy (the move admission is
// gated on move-linearity, not merely on the seed being a Map.set result). The kept copy makes `bump`'s
// accumulator independent, so `bump` overwriting key 0's value to 999 in place cannot corrupt `w`, and
// `keep` still reads the ORIGINAL value 100. If the copy were wrongly elided, `bump` would rewrite w's
// node in place and `keep` would read the corrupted 999 — so `100 999` (not `999 999`) proves the
// decline and the soundness of the elision gate.
import Ashes.Map
import Ashes.IO
import Ashes.Text
let cmp a b =
    if a == b
    then 0
    else
        if a <= b
        then -1
        else 1

let valOf d k m =
    match Ashes.Map.get(cmp)(k)(m) with
        | None -> d
        | Some(v) -> v

let recursive bump n acc =
    if n <= 0
    then acc
    else bump(n - 1)(Ashes.Map.set(cmp)(0)(999)(acc))

let w = Ashes.Map.set(cmp)(0)(100)(Ashes.Map.empty)

let keep = w

let bumped = bump(3)(w)
in Ashes.IO.print(Ashes.Text.fromInt(valOf(-1)(0)(keep)) + " " + Ashes.Text.fromInt(valOf(-1)(0)(bumped)))
