let recursive containsInt (x: Int) (xs: List(Int)) =
    match xs with
        | [] -> false
        | y :: rest -> y == x || containsInt(x)(rest)

let recursive storedValuesOf (cell: Int) (instructions: List((Int, Int))) (values: List(Int)) =
    match instructions with
        | [] -> values
        | (pointer, source) :: rest ->
            if pointer == cell
            then storedValuesOf(cell)(rest)(source :: values)
            else storedValuesOf(cell)(rest)(values)

let recursive withdrawCellRetains (cell: Int) (slots: List(Int)) (visited: List(Int)) (instructions: List((Int, Int))) =
    if containsInt(cell)(visited)
    then (instructions, visited)
    else withdrawStoredRetains(storedValuesOf(cell)(instructions)([]))(slots)(cell :: visited)(instructions)
and withdrawStoredRetains (values: List(Int)) (slots: List(Int)) (visited: List(Int)) (instructions: List((Int, Int))) =
    match values with
        | [] -> (instructions, visited)
        | value :: rest ->
            if containsInt(value)(slots)
            then withdrawStoredRetains(rest)(slots)(visited)((value, 0) :: instructions)
            else
                if value > 100
                then
                    match withdrawCellRetains(value)(slots)(visited)(instructions) with
                        | (nested, nestedVisited) -> withdrawStoredRetains(rest)(slots)(nestedVisited)(nested)
                else withdrawStoredRetains(rest)(slots)(visited)(instructions)

let recursive withdrawCells (cells: List(Int)) (slots: List(Int)) (visited: List(Int)) (instructions: List((Int, Int))) =
    match cells with
        | [] -> instructions
        | cell :: rest ->
            match withdrawCellRetains(cell)(slots)(visited)(instructions) with
                | (withdrawn, nextVisited) -> withdrawCells(rest)(slots)(nextVisited)(withdrawn)

let recursive total (k: Int) (xs: List((Int, Int))) =
    match xs with
        | [] -> k
        | (a, b) :: rest -> total(k + a + b)(rest)

let probe (n: Int) = withdrawCells([28, 29])([n % 5])([])([(28, 128 + n % 5), (29, 7)]) |> total(0)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in Ashes.IO.print(Ashes.Text.fromInt(loop(ROUNDS)(0)))
