import Ashes.Result
let parse = 
    fun (text) -> 
        if text == "42"
        then Ok(42)
        else Error(0)
in 
    match Ashes.Result.map(fun (n) -> n + 1)(parse("42")) with
        | Ok(value) -> Ashes.IO.print(value)
        | Error(code) -> Ashes.IO.print(code)
