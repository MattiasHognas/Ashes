type Binder =
    | key: Int
    | uses: List(Int)

type Walk =
    | selfName: Str
    | binders: List(Binder)

let bump (walk: Walk) = walk with binders = Binder(key = 1, uses = [1]) :: walk.binders

let apply (f: Walk -> Walk) (walk: Walk) = f(walk)

let recursive countUses (binders: List(Binder)) (total: Int) =
    match binders with
        | [] -> total
        | Binder { uses = uses } :: rest -> countUses(rest)(total + Ashes.Collection.List.length(uses))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match Walk(selfName = "walk", binders = [])
        |> apply(bump)
        |> apply(bump) with
            | Walk { binders = binders } -> rounds(n - 1)(total + countUses(binders)(0))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
