// Large-list scope-exit copy-out: a top-level let binding a list past ~1M cells used to cache the
// heads in an unbounded dynamic stack alloca (8 bytes per cell) during CopyOutList — two such
// copies in the entry frame overflowed the 8 MB stack (segfault). Large caches now spill to OS
// memory. Exercises both the direct binding copy and a second copy of the reversed list.
// expect: 1500000 1 1500000
import Ashes.IO as io
import Ashes.Collection.List as list
import Ashes.Text as text
let recursive build i acc =
    if i == 0
    then acc
    else build(i - 1)(i :: acc)

let xs = build(1500000)([])

let ys = list.reverse(xs)

let headOf zs =
    match zs with
        | h :: _ -> h
        | [] -> 0

io.print(text.fromInt(list.length(xs)) + " " + text.fromInt(headOf(xs)) + " " + text.fromInt(headOf(ys)))
