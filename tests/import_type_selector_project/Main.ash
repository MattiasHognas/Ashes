// expect: 2 at:at:a T
import Shapes.Node
let recursive depth (n: Node) =
    match n with
        | TypeAt(_span, inner) -> 1 + depth(inner)
        | Leaf(_text) -> 0

let x =
    Leaf("a")
    |> TypeAt(2)
    |> TypeAt(1)

Ashes.IO.print(Ashes.Text.fromInt(depth(x)) + " " + describe(x) + " " + (if x == Shapes.TypeAt(1)(TypeAt(2)(Leaf("a")))
then "T"
else "F"))
