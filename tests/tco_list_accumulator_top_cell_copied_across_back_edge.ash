// expect: 90
type E =
    | Bad(Int, Str)

let check (x: Int) =
    if x > 5
    then
        "big"
        |> Bad(x)
        |> Error
    else Ok(x)

let recursive go (n: Int) (acc: List(Int)) =
    if n == 0
    then Ok(acc)
    else
        if n == 100
        then go(n - 1)(acc)
        else
            match check(n) with
                | Error(error) -> Error(error)
                | Ok(v) -> go(n - 1)(v :: acc)

let describe result =
    match result with
        | Ok(items) ->
            match items with
                | [] -> 0
                | head :: _ -> head
        | Error(Bad(x, _text)) -> x + 1

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + describe(go(n % 9)([])))

0
|> rounds(30)
|> Ashes.IO.print
