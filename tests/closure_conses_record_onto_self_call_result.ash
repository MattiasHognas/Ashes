// expect: 24
// A recursive producer builds each record in a pipe and conses it, inside a lambda, onto its own
// curried self call reached through the lambda's capture: the call's result is owned, the cell
// is reference-counted, and the record takes a reference of its own.
type LeakEntry =
    | slot: Int
    | live: Bool

let makeEntry (value: Int) = LeakEntry(slot = value, live = value % 2 == 0)

let recursive stepEntries (base: Int) (remaining: List(Int)) =
    match remaining with
        | [] -> []
        | value :: rest ->
            value + base
            |> makeEntry
            |> (given (made) -> made :: stepEntries(base)(rest))

let recursive liveInOf (slot: Int) (entries: List(LeakEntry)) =
    match entries with
        | [] -> false
        | LeakEntry { slot = candidate, live = live } :: rest ->
            if candidate == slot
            then live
            else liveInOf(slot)(rest)

let recursive stepLive (entries: List(LeakEntry)) (remaining: List(Int)) =
    match remaining with
        | [] -> []
        | slot :: rest ->
            entries
            |> liveInOf(slot + 1)
            |> (given (liveOut) -> LeakEntry(slot = slot, live = liveOut || slot == 2))
            |> (given (made) -> made :: stepLive(entries)(rest))

let recursive countEntries (entries: List(LeakEntry)) (total: Int) =
    match entries with
        | [] -> total
        | LeakEntry { slot = found, live = true } :: rest -> countEntries(rest)(total + found)
        | _ :: rest -> countEntries(rest)(total)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + countEntries(stepEntries(1)([1, 2, 3, 4]))(0) + countEntries(stepLive([])([1, 2, 3, 4]))(0))

0
|> rounds(3)
|> Ashes.IO.print
