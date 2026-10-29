// expect: 4000
let recursive keepUntil (stop: Str) (items: List(Str)) =
    match items with
        | entry :: rest ->
            if entry == stop
            then rest
            else entry :: keepUntil(stop)(rest)
        | [] -> []

let recursive words (n: Int) (acc: List(Str)) =
    if n <= 0
    then acc
    else words(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.List.length([]
        |> words(5)
        |> keepUntil("3")))

0
|> rounds(1000)
|> Ashes.IO.print
