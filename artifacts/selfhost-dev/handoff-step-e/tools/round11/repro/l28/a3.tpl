let recursive containsInt (x: Int) (xs: List(Int)) =
    match xs with
        | [] -> false
        | y :: rest -> y == x || containsInt(x)(rest)

let helper (cell: Int) (visited: List(Int)) (instructions: List((Int, Int))) =
    if containsInt(cell)(visited)
    then (instructions, visited)
    else ((cell, 1) :: instructions, cell :: visited)

let recursive withdrawStoredRetains (values: List(Int)) (slots: List(Int)) (visited: List(Int)) (instructions: List((Int, Int))) =
    match values with
        | [] -> (instructions, visited)
        | value :: rest ->
            if containsInt(value)(slots)
            then withdrawStoredRetains(rest)(slots)(visited)((value, 0) :: instructions)
            else
                if value > 100
                then
                    match helper(value)(visited)(instructions) with
                        | (nested, nestedVisited) -> withdrawStoredRetains(rest)(slots)(nestedVisited)(nested)
                else withdrawStoredRetains(rest)(slots)(visited)(instructions)

let recursive total (k: Int) (xs: List((Int, Int))) =
    match xs with
        | [] -> k
        | (a, b) :: rest -> total(k + a + b)(rest)

let probe (n: Int) =
    match withdrawStoredRetains([128 + n % 5, 7, 140])([n % 5])([28])([(28, 3)]) with
        | (instructions, visited) -> total(Ashes.Collection.List.length(visited))(instructions)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in Ashes.IO.print(Ashes.Text.fromInt(loop(ROUNDS)(0)))
