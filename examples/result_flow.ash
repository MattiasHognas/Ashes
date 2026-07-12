import Ashes.Result
let parseOr text =
    if text == "42"
    then Ok(42)
    else Error("not-42")
in
    "42"
    |> parseOr
    |> Ashes.Result.map(given (n) -> n + 1)
    |> Ashes.Result.default(0)
    |> Ashes.IO.print
