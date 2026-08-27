// expect: [Named(1, "a", [Leaf])]|true|false
type T =
    | Leaf
    | Named(Int, Str, List(T))
    deriving {Eq, Show}

let x = Named(1)("a")([Leaf])

let y = Named(1)("a")([Leaf])

let z = Named(2)("b")([])

let equalResult =
    if x == y
    then "true"
    else "false"

let notEqualResult =
    if x == z
    then "true"
    else "false"

Ashes.IO.print(Ashes.Trait.Show.show([x]) + "|" + equalResult + "|" + notEqualResult)
