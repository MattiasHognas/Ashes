// expect: 42
// fmt-skip: `ashes fmt` mangles selector imports (import M.binding -> import M as binding)
import Ashes.IO.print
import Ashes.Result.map
match map(fun (n) -> n + 1)(Ok(41)) with
    | Ok(value) -> print(value)
    | Error(_) -> print(0)
