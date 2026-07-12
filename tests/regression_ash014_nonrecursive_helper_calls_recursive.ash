// A non-recursive helper (rotateFirst) that calls a recursive helper (insertAt), itself called
// from a later recursive function (spin) matching an ADT, previously tripped a spurious
// ASH014 "insertAt is not yet declared" -- insertAt is declared earlier and is a valid backward
// reference. Regression guard for the fix in LowerVar (top-level-function-ref reconstruction is no
// longer gated on being inside a reuse specialization).
// expect: 213
import Ashes.IO as io
import Ashes.Text as text
let recursive insertAt i v xs = 
    if i == 0
    then v :: xs
    else 
        match xs with
            | [] -> v :: []
            | h :: t -> h :: insertAt(i - 1)(v)(t)

let rotateFirst r xs = 
    match xs with
        | [] -> []
        | h :: t -> insertAt(r)(h)(t)

type Box =
    | B(List(Int))

let recursive spin n box = 
    if n == 0
    then box
    else 
        match box with
            | B(xs) -> spin(n - 1)(B(rotateFirst(1)(xs)))

let recursive show xs = 
    match xs with
        | [] -> ""
        | h :: t -> text.fromInt(h) + show(t)

io.print(match spin(1)(B([1, 2, 3])) with
    | B(xs) -> show(xs))
