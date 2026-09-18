// expect: 3|none|forty-two|12|seven
import Ashes.Collection.Map
let recursive fill (n: Int) (map: MapTree(Int, Str)) =
    if n == 0
    then map
    else
        map
        |> Ashes.Collection.Map.setInt(n * 7)(Ashes.Text.fromInt(n))
        |> fill(n - 1)

let filled = fill(12)(Ashes.Collection.Map.empty)

let describe (found: Maybe(Str)) =
    match found with
        | Some(text) -> text
        | None -> "none"

let replaced = Ashes.Collection.Map.setInt(42)("forty-two")(filled)

let withSeven = Ashes.Collection.Map.setInt(7)("seven")(replaced)

Ashes.IO.print(describe(Ashes.Collection.Map.getInt(21)(filled)) + "|" + describe(Ashes.Collection.Map.getInt(5)(filled)) + "|" + describe(Ashes.Collection.Map.getInt(42)(replaced)) + "|" + Ashes.Text.fromInt(Ashes.Collection.Map.size(withSeven)) + "|" + describe(Ashes.Collection.Map.getInt(7)(withSeven)))
