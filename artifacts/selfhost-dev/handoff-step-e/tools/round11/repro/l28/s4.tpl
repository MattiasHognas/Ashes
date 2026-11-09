let recursive storedValuesOf (cell: Int) (instructions: List(Int)) (values: List(Int)) =
    match instructions with
        | [] -> values
        | source :: rest -> storedValuesOf(cell)(rest)(source :: values)

let recursive withdrawStoredRetains (values: List(Int)) (slots: List(Int)) =
    match values with
        | [] -> slots
        | _value :: rest -> slots
and withdrawCellRetains (cell: Int) (slots: List(Int)) (instructions: List(Int)) = withdrawStoredRetains(storedValuesOf(cell)(instructions)([]))(slots)

let recursive total (k: Int) (xs: List(Int)) =
    match xs with
        | [] -> k
        | a :: rest -> total(k + a)(rest)

let probe (n: Int) = withdrawCellRetains(28)([n % 5])([7, n]) |> total(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in Ashes.IO.print(Ashes.Text.fromInt(loop(ROUNDS)(0)))
