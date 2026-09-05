// expect: 5003 5008 15002
// A string built by an affine append loop is large enough to live in its own OS mapping, so
// releasing it too early is a segfault rather than a silent reuse. Embedding it in a list literal
// (a cons cell that keeps no reference of its own), passing that list to a consuming loop, and
// joining a list that carries it through the callee's result must all keep the string alive until
// the last read of the cell or the copied-out result.
import Ashes.Text as text
let recursive build n acc =
    if n == 0
    then acc
    else build(n - 1)(acc + "x")

let recursive sumLengths items acc =
    match items with
        | [] -> acc
        | head :: rest -> sumLengths(rest)(acc + text.byteLength(head))

let recursive rev acc xs =
    match xs with
        | [] -> acc
        | head :: tail -> rev(head :: acc)(tail)

let recursive interleave separator acc ps =
    match ps with
        | [] -> rev([])(acc)
        | first :: rest ->
            match acc with
                | [] -> interleave(separator)(first :: [])(rest)
                | _ -> interleave(separator)(first :: separator :: acc)(rest)

let line = build(5000)("")

let embedded = sumLengths([line, "def"])(0)

let interleaved = sumLengths(interleave("\n")([])([line, "def", "ghi"]))(0)

let joined = text.byteLength(text.join("\n")([line, line, line]))

Ashes.IO.print(text.fromInt(embedded) + " " + text.fromInt(interleaved) + " " + text.fromInt(joined))
