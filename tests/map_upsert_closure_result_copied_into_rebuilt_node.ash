// expect: 44

import Ashes.Collection.Map.MapTree
let recursive rounds (n: Int) (total: Int) (counts: MapTree(Str, Str)) =
    if n == 0
    then total + Ashes.Collection.Map.size(counts)
    else
        counts
        |> Ashes.Collection.Map.upsertStr("k" + Ashes.Text.fromInt(n % 4))("0")(given (existing: Str) -> Ashes.Text.fromInt((Ashes.Text.byteLength(existing) + n) % 10))
        |> rounds(n - 1)(total + 1)

Ashes.Collection.Map.empty
|> rounds(40)(0)
|> Ashes.IO.print
