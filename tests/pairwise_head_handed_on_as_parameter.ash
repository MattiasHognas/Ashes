// expect: 18
let recursive collect (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive pairs (prev: Str) (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest -> pairs(first)(prev :: first :: acc)(rest)

let recursive count (xs: List(Str)) (total: Int) =
    match xs with
        | [] -> total
        | _ :: tail -> count(tail)(total + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + count([]
        |> collect(3)
        |> pairs(Ashes.Text.fromInt(n))([]))(0))

0
|> rounds(3)
|> Ashes.IO.print
