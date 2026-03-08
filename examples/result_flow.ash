import Ashes.Result
let parseOr = 
    fun (text) -> 
        if text == "42"
        then Ok(42)
        else Error("not-42")
in 
    "42"
    |> parseOr
    |> Ashes.Result.map(fun (n) -> n + 1)
    |> Ashes.Result.default(0)
    |> Ashes.IO.print
