// expect: h=h,h;g=g,g;a=a,a
import Ashes.IO
import Ashes.Collection.Map
import Ashes.Collection.List
let recursive addAll (names: List(Str)) (index: MapTree(Str, List(Str))) =
    match names with
        | [] -> index
        | name :: rest ->
            index
            |> Ashes.Collection.Map.upsertStr(name)([name])(given (existing: List(Str)) -> name :: existing)
            |> addAll(rest)

let render (entry: (Str, List(Str))) =
    match entry with
        | (key, values) -> key + "=" + Ashes.Text.join(",")(values)

Ashes.Collection.Map.empty
|> addAll(["g", "h", "h", "g", "a", "a"])
|> Ashes.Collection.Map.toList
|> Ashes.Collection.List.map(render)
|> Ashes.Text.join(";")
|> Ashes.IO.print
