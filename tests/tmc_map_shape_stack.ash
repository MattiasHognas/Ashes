// A map written at a concrete element type is the tail-modulo-constructor shape and is transformed,
// so 400000 elements cost no stack at all — the untransformed version needs about 209 bytes each and
// faults well below this size on an ordinary 8 MiB stack. The shipped generic Ashes.Collection.List.map
// is the same shape at an element type that is still a variable, which no reference-counted cell can
// own, so its own body stays declined; the call below fixes that type, so it runs an element-specialized
// copy instead, folded here at a size the generic recursion it replaces also handles — pinning that the
// copy still builds a correct, shareable list.
// expect: 160001200000 5000050000
import Ashes.Collection.List as list
import Ashes.IO as io
import Ashes.Text as text
let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(count :: acc)

let recursive mapIncrement values =
    match values with
        | [] -> []
        | head :: tail -> head + 1 :: mapIncrement(tail)

let increment value = value + 1

let add left right = left + right

let transformed =
    []
    |> build(400000)
    |> mapIncrement

let specialized =
    []
    |> build(100000)
    |> list.map(increment)

text.fromInt(list.foldLeft(add)(0)(transformed) + list.foldLeft(add)(0)(transformed)) + " " + text.fromInt(list.foldLeft(add)(0)(specialized) - 100000) |> io.print
