// expect: true
import Ashes.Core.Result
Ok(1)
|> Result.isOk
|> Ashes.IO.print
