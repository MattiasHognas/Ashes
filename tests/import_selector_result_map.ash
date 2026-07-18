// expect: 42
import Ashes.IO.print
import Ashes.Core.Result.map
match map(given (n) -> n + 1)(Ok(41)) with
    | Ok(value) -> print(value)
    | Error(_) -> print(0)
