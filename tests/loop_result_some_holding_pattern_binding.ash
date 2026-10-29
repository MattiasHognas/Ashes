// expect: 30000
// A loop over a table returns Some of the entry it finds, a list a pattern binds out of the
// loop's list parameter, and a helper rebuilds the Some around the entry it looked up. Both cells
// are built on the reference-counted heap, so each round's entries are released and still read
// back whole.
let recursive lookup (key: Str) (table: List((Str, List(Int)))) =
    match table with
        | [] -> None
        | (name, entry) :: rest ->
            if name == key
            then Some(entry)
            else lookup(key)(rest)

let handOff (table: List((Str, List(Int)))) (key: Str) =
    match lookup(key)(table) with
        | Some(entry) -> Some(entry)
        | None -> None

let recursive build (n: Int) (acc: List((Str, List(Int)))) =
    if n == 0
    then acc
    else build(n - 1)(("k" + Ashes.Text.fromInt(n), [n, n + 1, n + 2]) :: acc)

let count (table: List((Str, List(Int)))) =
    (match lookup("k1")(table) with
        | Some(entry) -> Ashes.Collection.List.length(entry)
        | None -> 0) + (match handOff(table)("k2") with
        | Some(entry) -> Ashes.Collection.List.length(entry)
        | None -> 0)

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else
        rounds(n - 1)(sum + count(build(3)([])))

0
|> rounds(5000)
|> Ashes.IO.print
