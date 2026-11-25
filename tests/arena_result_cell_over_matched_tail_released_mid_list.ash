// expect: 3
// The arm returning `(key, value) :: tail` over its matched tail is reached past an entry the loop
// keeps: the result cell is built in the arena with a reference of its own to the tail, and the
// copy that normalizes the result holds its own, so the cell's is released at the exit. Replacing
// an entry in the middle of the list left the entries after it behind.
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

[("xx", [1]), ("a", [1]), ("ccc", [2])]
|> setAssociation("a")([3])
|> (given (saved) -> count(saved)(0))
|> Ashes.Text.fromInt
|> Ashes.IO.print
