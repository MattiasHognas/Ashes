import Result
let parseOr = 
    fun (x) -> 
        if x >= 0
        then Ok(x)
        else Error("neg")
in Ashes.IO.print(Result.default(0)(parseOr(5)))
