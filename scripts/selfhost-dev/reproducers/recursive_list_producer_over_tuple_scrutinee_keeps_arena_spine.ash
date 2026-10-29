let recursive keep (ownership: List(Str)) =
    match (ownership, 0) with
        | (entry :: rest, _) -> entry :: keep(rest)
        | _ -> []

let recursive total (xs: List(Str)) =
    match xs with
        | [] -> 0
        | _x :: rest -> 1 + total(rest)

let recursive rounds (ownership: List(Str)) (n: Int) (sum: Int) =
    if n == 0
    then sum
    else
        rounds(ownership)(n - 1)(sum + total(keep(ownership)))

0
|> rounds(["a", "b"])(ROUNDS)
|> Ashes.IO.print
