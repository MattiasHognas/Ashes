import Ashes.Result
let parseOr x =
    if x >= 0
    then Ok(x)
    else Error("neg")
in Ashes.IO.print(Result.default(0)(parseOr(5)))
