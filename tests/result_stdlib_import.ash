// expect: 42
import Ashes.Core.Result
import Ashes.IO
match map(given (n) -> n + 1)(Ok(41)) with
    | Ok(value) -> print(value)
    | Error(_) -> print(0)
