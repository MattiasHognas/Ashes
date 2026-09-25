// expect: 1 1 1 k1

let recursive keysOf (n: Int) (keys: MapTree(Str, Bool)) =
    if n == 0
    then keys
    else
        keys
        |> Ashes.Collection.Map.setStr("k" + Ashes.Text.fromInt(n))(true)
        |> keysOf(n - 1)

let m = keysOf(1)(Ashes.Collection.Map.empty)

let count =
    Ashes.Collection.Map.foldLeft(given (acc: Int) ->
        given (_k: Str) ->
            given (_v: Bool) -> acc + 1)(0)(m)

let keys =
    Ashes.Collection.Map.foldLeft(given (acc: List(Str)) ->
        given (k: Str) ->
            given (_v: Bool) -> k :: acc)([])(m)

let entries = Ashes.Collection.Map.toList(m)

Ashes.IO.print(Ashes.Text.fromInt(count) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(keys)) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(entries)) + " " + (match entries with
    | [] -> "empty"
    | (k, _) :: _ -> k))
