// expect: 4000
let recursive keep (marker: Str) (items: List((Str, Int))) =
    match items with
        | entry :: rest -> entry :: keep(marker)(rest)
        | [] -> []

let recursive pairs (n: Int) (acc: List((Str, Int))) =
    if n <= 0
    then acc
    else pairs(n - 1)((Ashes.Text.fromInt(n), n) :: acc)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.List.length([]
        |> pairs(4)
        |> keep("x")))

0
|> rounds(1000)
|> Ashes.IO.print
