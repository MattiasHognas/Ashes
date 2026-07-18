// expect: 1
import Ashes.Core.Result
match Result.map(given (x) -> x + 1)(Error(1)) with
    | Ok(_) -> Ashes.IO.print(0)
    | Error(e) -> Ashes.IO.print(e)
