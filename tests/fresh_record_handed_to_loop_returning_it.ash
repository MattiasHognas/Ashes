// A fresh record handed to a loop that may return it whole is released by the caller unless the
// loop returned it.
// expect: 2000
type Binder =
    | key: Int
    | uses: List(Int)

type Walk =
    | selfName: Str
    | binders: List(Binder)

type Node =
    | Leaf(Int)
    | Pair(Node, Node)

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

let recursive countUses (binders: List(Binder)) (total: Int) =
    match binders with
        | [] -> total
        | Binder { uses = uses } :: rest -> countUses(rest)(total + Ashes.Collection.List.length(uses))

let tree = Pair(Leaf(1))(Leaf(2))

let fresh (round: Int) = Walk(selfName = "walk", binders = [Binder(key = 1, uses = []), Binder(key = 2, uses = [])])

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
