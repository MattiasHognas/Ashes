// expect: 5
import Ashes.Core.Result
Error("nope")
|> Result.default(5)
|> Ashes.IO.print
