// expect: 40040 40039
// A map threaded through a loop by a helper's owned result releases each earlier version once
// the successor holds the new one, and keeps it while another parameter still names it.
let keepFirst (key: Str) (value: List(Str)) (map: MapTree(Str, List(Str))) =
    Ashes.Collection.Map.upsertStr(key)(value)(given (existing) -> existing)(map)

let recursive build (n: Int) (current: MapTree(Str, List(Str))) =
    if n == 0
    then current
    else
        current
        |> keepFirst("k" + Ashes.Text.fromInt(n))(["v" + Ashes.Text.fromInt(n)])
        |> build(n - 1)

let recursive buildKeepingPrevious (n: Int) (current: MapTree(Str, List(Str))) (previous: MapTree(Str, List(Str))) =
    if n == 0
    then Ashes.Collection.Map.size(current) * 1000 + Ashes.Collection.Map.size(previous)
    else
        buildKeepingPrevious(n - 1)(keepFirst("k" + Ashes.Text.fromInt(n))(["v"])(current))(current)

let built = build(40)(Ashes.Collection.Map.empty)

let kept = buildKeepingPrevious(40)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty)

Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.Map.size(built) * 1000 + Ashes.Collection.Map.size(build(40)(built))) + " " + Ashes.Text.fromInt(kept))
