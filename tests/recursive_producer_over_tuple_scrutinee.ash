// expect: 3000
let recursive keep (ownership: List(Str)) =
    match (ownership, 0) with
        | (entry :: rest, _) -> entry :: keep(rest)
        | _ -> []

let recursive words (n: Int) (acc: List(Str)) =
    if n <= 0
    then acc
    else words(n - 1)(Ashes.Text.fromInt(n) :: acc)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.List.length([]
        |> words(3)
        |> keep))

0
|> rounds(1000)
|> Ashes.IO.print
