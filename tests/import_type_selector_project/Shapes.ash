type Node =
    | TypeAt(Int, Node)
    | Leaf(Str)
    deriving {Eq}

let recursive describe (n: Node) =
    match n with
        | TypeAt(_span, inner) -> "at:" + describe(inner)
        | Leaf(text) -> text
