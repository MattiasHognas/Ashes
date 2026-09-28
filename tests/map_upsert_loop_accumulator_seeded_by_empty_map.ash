// A loop threads a map seeded by `Map.empty` through a helper that upserts into it: the map is
// handed on once per path and the seed is the map type's only nullary constructor, so the loop's
// accumulator is uniquely owned and the upsert is routed to the map's reuse specialization.
// expect: 120
import Ashes.Collection.Map
let keepFirst (key: Str) value map =
    Ashes.Collection.Map.upsertStr(key)(value)(given (existing) -> existing)(map)

let recursive adjacencyOf (n: Int) (adj: MapTree(Str, List(Str))) =
    if n == 0
    then adj
    else
        adj
        |> keepFirst("k" + Ashes.Text.fromInt(n))(["t" + Ashes.Text.fromInt(n)])
        |> adjacencyOf(n - 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.Map.size(adjacencyOf(40)(Ashes.Collection.Map.empty)))

0
|> rounds(3)
|> Ashes.IO.print
