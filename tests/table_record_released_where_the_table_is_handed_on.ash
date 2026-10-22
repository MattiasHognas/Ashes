// A loop threads a map of records, merging each key's earlier record into the map or handing the
// map on unchanged once the record is full. The map is only ever the parameter itself or a call's
// owned result, which holds the earlier record's parts by references of its own, so the earlier
// record is released on the edge that hands the map on unchanged.
// expect: 30
type Reach =
    | fresh: Bool
    | parts: List(Str)

let lookup (key: Str) (table: MapTree(Str, Reach)) =
    match Ashes.Collection.Map.getStr(key)(table) with
        | Some(found) -> found
        | None -> Reach(fresh = false, parts = [])

let recursive solve (table: MapTree(Str, Reach)) (keys: List(Str)) (fuel: Int) =
    match keys with
        | [] -> table
        | key :: rest ->
            if fuel <= 0
            then table
            else
                let previous = lookup(key)(table)
                in
                    let merged = Reach(fresh = true, parts = key :: previous.parts)
                    in
                        if Ashes.Collection.List.length(previous.parts) > 1
                        then solve(table)(rest)(fuel - 1)
                        else
                            solve(Ashes.Collection.Map.setStr(key)(merged)(table))(rest)(fuel - 1)

let run (seed: Int) =
    Ashes.Collection.Map.size(solve(Ashes.Collection.Map.empty)(["flint", "coal", "flint", "flint", "coal", "flint", "flint"])(11)) + seed

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(8))

0
|> rounds(3)
|> Ashes.IO.print
