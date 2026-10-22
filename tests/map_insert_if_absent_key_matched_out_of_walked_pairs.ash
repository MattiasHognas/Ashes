// expect: 24

let recursive addDependents (function: Str) (dependencies: List((Str, Bool))) (dependents: MapTree(Str, List(Str))) =
    match dependencies with
        | [] -> dependents
        | (dependency, _present) :: rest ->
            (match Ashes.Collection.Map.getStr(dependency)(dependents) with
                | Some(_existing) -> dependents
                | None -> Ashes.Collection.Map.setStr(dependency)([function])(dependents)) |> addDependents(function)(rest)

let recursive keysOf (n: Int) (keys: MapTree(Str, Bool)) =
    if n == 0
    then keys
    else
        keys
        |> Ashes.Collection.Map.setStr("k" + Ashes.Text.fromInt(n))(true)
        |> keysOf(n - 1)

let recursive dependentsOf (n: Int) (dependents: MapTree(Str, List(Str))) =
    if n == 0
    then dependents
    else
        dependents
        |> addDependents("f" + Ashes.Text.fromInt(n))(Ashes.Collection.Map.empty
        |> keysOf(8)
        |> Ashes.Collection.Map.toList)
        |> dependentsOf(n - 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.Map.size(dependentsOf(20)(Ashes.Collection.Map.empty)))

0
|> rounds(3)
|> Ashes.IO.print
