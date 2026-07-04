// expect: 400000|400089
// A tail-recursive fold that threads a heap value (`tag`, a Bytes) UNCHANGED through every iteration is
// loop-invariant: it holds the value passed into the loop (below the arena watermark), so a per-iteration
// arena reset leaves it valid and the loop stays constant-memory instead of either never resetting (leak)
// or copying the invariant value out every iteration. This exercises the reset-safety path with a
// growing (unbounded) key set so the accumulator tree relocates each iteration while `tag` is read every
// iteration -- if the invariant arg were wrongly treated it would dangle and corrupt the reads. 'Z' = 90,
// so key "399999" holds 90 + 399999 = 400089, over 400000 distinct keys.
import Ashes.IO
import Ashes.Text
import Ashes.Map
import Ashes.String
import Ashes.Bytes
import Ashes.UInt
let recursive loop tag i n m = 
    if i >= n
    then m
    else 
        let b0 = Ashes.UInt.toInt(Ashes.Bytes.get(tag)(0))
        in 
            let key = Ashes.Text.fromInt(i)
            in loop(tag)(i + 1)(n)(Ashes.Map.set(Ashes.String.compare)(key)(b0 + i)(m))

let tag = Ashes.Bytes.fromText("Z")

let final = loop(tag)(0)(400000)(Ashes.Map.empty)
in 
    Ashes.IO.print(Ashes.Text.fromInt(Ashes.Map.size(final)) + "|" + (match Ashes.Map.get(Ashes.String.compare)("399999")(final) with
        | Some(v) -> Ashes.Text.fromInt(v)
        | None -> "?"))
