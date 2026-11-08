// expect: 50
// A function loops through wrappers of its variant parameter and, at a call node, conses the call's
// argument, a part of the parameter, onto the list of the record its recursive call returned; the
// duplicate the consed cell took of the argument is released behind the copy the return makes.
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
    match NodeName("n")
    |> NodeCall(NodeCall(NodeName("walk" + Ashes.Text.fromInt(k)))(NodeName("rest" + Ashes.Text.fromInt(k))))
    |> NodeAt(1)
    |> collectSpine with
        | Spine { root = root, arguments = arguments } -> isName(root) + 2 * Ashes.Collection.List.length(arguments)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.Text.fromInt
|> Ashes.IO.print
