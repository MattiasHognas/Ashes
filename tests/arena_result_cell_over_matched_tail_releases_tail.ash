// expect: 2
// A loop whose matching arm returns `(key, value) :: tail` over the tail it matched builds that cell in
// the arena, taking a reference of its own to the tail; the copy that normalizes the result holds
// its own references, so the cell's is released at the exit. Replacing the head of a two-entry
// list left the whole remaining entry behind.
let recursive setAssociation (key: Str) (value: List(Int)) (entries: List((Str, List(Int)))) =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if Ashes.Text.byteLength(k) == Ashes.Text.byteLength(key)
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive count (saved: List((Str, List(Int)))) (acc: Int) =
    match saved with
        | [] -> acc
        | (_, snapshots) :: rest -> count(rest)(acc + Ashes.Collection.List.length(snapshots))

[("a", [1]), ("cc", [2])]
|> setAssociation("a")([3])
|> (given (saved) -> count(saved)(0))
|> Ashes.Text.fromInt
|> Ashes.IO.print
