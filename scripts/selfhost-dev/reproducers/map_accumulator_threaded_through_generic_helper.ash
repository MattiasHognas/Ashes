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
|> rounds(ROUNDS)
|> Ashes.IO.print
