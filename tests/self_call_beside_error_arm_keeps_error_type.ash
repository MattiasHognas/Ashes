// expect: 1930

// A loop returns a Result whose error arm forwards a helper's error, beside a tail self call in
// the branch lowered first. The self call has the loop's own result type, so the loop's error type
// is the helper's and the error's string is released with it.
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
        | Error(Bad(x, text)) -> x + Ashes.Text.length(text)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + describe(go(n % 9)([])))

0
|> rounds(500)
|> Ashes.IO.print
