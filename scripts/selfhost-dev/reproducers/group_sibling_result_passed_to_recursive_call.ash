type Walk =
    | name: Str
    | uses: List(Int)

type Node =
    | Leaf(Int)
    | Pair(Node, Node)
    | Call(Node, List(Node))

let recursive walkNode (node: Node) (depth: Int) (walk: Walk) =
    match node with
        | Leaf(key) -> walk with uses = key + depth :: walk.uses
        | Pair(left, right) ->
            walk
            |> walkNode(left)(depth + 1)
            |> walkNode(right)(depth + 1)
        | Call(root, arguments) ->
            walk
            |> walkArguments(arguments)(depth)
            |> walkNode(root)(depth)
and walkArguments (arguments: List(Node)) (depth: Int) (walk: Walk) =
    match arguments with
        | [] -> walk
        | argument :: rest ->
            walk
            |> walkNode(argument)(depth)
            |> walkArguments(rest)(depth)

let tree = Call(Leaf(1))([])

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match Walk(name = "walk" + Ashes.Text.fromInt(n), uses = []) |> walkNode(tree)(0) with
            | Walk { uses = uses } -> rounds(n - 1)(total + Ashes.Collection.List.length(uses))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
