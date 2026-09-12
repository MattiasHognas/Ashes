// expect: true
import Ashes.Core.Result
Error("boom")
|> Result.isError
|> Ashes.IO.print
