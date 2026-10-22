// expect: 30
import Ashes.Collection.Map.MapTree
let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
    match values with
        | [] -> acc
        | value :: rest -> reverseAll(rest)(value :: acc)

let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> Ashes.Collection.Map.size(queued)
                | _ ->
                    solve(reverseAll(back)([]))([])(queued)(fuel)
        | key :: rest ->
            if fuel <= 0
            then Ashes.Collection.Map.size(queued)
            else
                match Ashes.Collection.Map.getStr(key)(queued) with
                    | Some(true) -> solve(rest)(back)(queued)(fuel - 1)
                    | _ ->
                        solve(rest)(key + "x" :: back)(Ashes.Collection.Map.setStr(key)(true)(queued))(fuel - 1)

let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(keys)(n - 1)(total + solve(keys)([])(Ashes.Collection.Map.empty)(10))

0
|> rounds(["a", "b", "c"])(3)
|> Ashes.IO.print
