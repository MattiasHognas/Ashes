// expect: 11
// withdrawCellRetains tail-calls its group sibling with a fresh list, the result of storedValuesOf.
// The group's tail calls run as one dispatch loop, which had none of an ordinary loop's parameter
// facts: the list went into an arena parameter and nothing released it. The dispatch loop is
// summarized like a source function now, and the list is released with the loop's parameter.
let recursive storedValuesOf (cell: Int) (instructions: List(Int)) (values: List(Int)) =
    match instructions with
        | [] -> values
        | source :: rest -> storedValuesOf(cell)(rest)(source :: values)

let recursive withdrawStoredRetains (values: List(Int)) (slots: List(Int)) =
    match values with
        | [] -> slots
        | _value :: _rest -> slots
and withdrawCellRetains (cell: Int) (slots: List(Int)) (instructions: List(Int)) =
    withdrawStoredRetains(storedValuesOf(cell)(instructions)([]))(slots)

let recursive total (k: Int) (xs: List(Int)) =
    match xs with
        | [] -> k
        | a :: rest -> total(k + a)(rest)

let probe (n: Int) =
    [7, n]
    |> withdrawCellRetains(28)([n % 5])
    |> total(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(6)
|> Ashes.Text.fromInt
|> Ashes.IO.print
