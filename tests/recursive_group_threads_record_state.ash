// A recursive group threading a record state through sibling calls, one returning its
// parameter whole in one arm: every intermediate state is released.
// expect: 6000
type Binder =
    | key: Int
    | uses: List(Int)

type Walk =
    | selfName: Str
    | parameters: List(Str)
    | binders: List(Binder)

type Node =
    | Leaf(Int)
    | Pair(Node, Node)
    | Call(Node, List(Node))

let recursive addUse (key: Int) (use: Int) (binders: List(Binder)) =
    match binders with
        | [] -> []
        | (Binder { key = candidate, uses = uses } as binder) :: rest ->
            if candidate == key
            then (binder with uses = use :: uses) :: rest
            else binder :: addUse(key)(use)(rest)

let recordUse (key: Int) (use: Int) (walk: Walk) =
    if key > 0
    then walk with binders = addUse(key)(use)(walk.binders)
    else walk

let recursive walkNode (node: Node) (depth: Int) (walk: Walk) =
    match node with
        | Leaf(key) -> recordUse(key)(depth)(walk)
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

let recursive countUses (binders: List(Binder)) (total: Int) =
    match binders with
        | [] -> total
        | Binder { uses = uses } :: rest -> countUses(rest)(total + Ashes.Collection.List.length(uses))

let tree = Call(Leaf(1))([Pair(Leaf(2))(Leaf(0)), Call(Leaf(3))([Leaf(1), Leaf(2)]), Leaf(3)])

let fresh (round: Int) = Walk(selfName = "walk" + Ashes.Text.fromInt(round), parameters = ["a", "b"], binders = [Binder(key = 1, uses = []), Binder(key = 2, uses = []), Binder(key = 3, uses = [])])

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match n
        |> fresh
        |> walkNode(tree)(0) with
            | Walk { binders = binders } -> rounds(n - 1)(total + countUses(binders)(0))

0
|> rounds(1000)
|> Ashes.IO.print
