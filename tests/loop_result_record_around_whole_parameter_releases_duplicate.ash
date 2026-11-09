// expect: 30
// A function loops through wrappers of its variant parameter and, in its catch-all arm, builds a
// record around the whole parameter; the duplicate the record took is released behind the copy the
// return makes.
type Node =
    | NodeName(Str)
    | NodeInt(Int)
    | NodeAt(Int, Node)
    | NodeCall(Node, Node)

type Spine =
    | root: Node
    | arguments: List(Node)

let recursive collectSpine (expression: Node) =
    match expression with
        | NodeAt(_span, inner) -> collectSpine(inner)
        | NodeCall(function, argument) ->
            match collectSpine(function) with
                | Spine { root = root, arguments = arguments } -> Spine(root = root, arguments = argument :: arguments)
        | root -> Spine(root = root, arguments = [])

let isName (expression: Node) =
    match expression with
        | NodeName(_name) -> 1
        | _ -> 0

let probe (k: Int) =
    match NodeInt(k)
    |> NodeCall(NodeCall(NodeName("walk" + Ashes.Text.fromInt(k)))(NodeName("rest")))
    |> NodeAt(1)
    |> collectSpine with
        | Spine { root = root, arguments = arguments } -> isName(root) + Ashes.Collection.List.length(arguments)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.Text.fromInt
|> Ashes.IO.print
