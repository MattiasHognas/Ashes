// expect: 36
let recursive solve (seen: MapTree(Str, Bool)) (front: List(Str)) (fuel: Int) =
    match front with
        | [] -> seen
        | key :: rest ->
            if fuel <= 0
            then seen
            else
                solve(seen
                |> Ashes.Collection.Map.setStr(key)(true)
                |> Ashes.Collection.Map.setStr(key)(false))(rest)(fuel - 1)

let run (seed: Int) =
    Ashes.Collection.Map.size(solve(Ashes.Collection.Map.empty)(["cinder", "ember", "smoke", "soot"])(7)) + seed

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(8))

0
|> rounds(3)
|> Ashes.IO.print
