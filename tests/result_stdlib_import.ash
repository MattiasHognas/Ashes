// expect: 42
import Ashes.Result
import Ashes.IO
match map(fun (n) -> n + 1)(Ok(41)) with
    | Ok(value) -> print(value)
    | Error(_) -> print(0)
