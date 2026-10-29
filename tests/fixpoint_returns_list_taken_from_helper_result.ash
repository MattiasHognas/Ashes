// A fixpoint loop over a helper loop that returns its list parameter inside a tuple: the exit
// hands back the list it took out of the helper's result, which holds its own reference, so the
// loop still releases its parameter.
// expect: 3 2 1 3
let recursive grow (groups: List(Int)) (known: List(Str)) (changed: Bool) =
    match groups with
        | [] -> (known, changed)
        | g :: tail ->
            if Ashes.Collection.List.length(known) < g
            then grow(tail)(Ashes.Text.fromInt(g) :: known)(true)
            else grow(tail)(known)(changed)

let recursive settle (groups: List(Int)) (known: List(Str)) =
    match grow(groups)(known)(false) with
        | (nextKnown, true) -> settle(groups)(nextKnown)
        | (nextKnown, false) -> nextKnown

let settled = settle([1, 2, 3])([])

Ashes.IO.print(Ashes.Text.join(" ")(settled) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(settled)))
