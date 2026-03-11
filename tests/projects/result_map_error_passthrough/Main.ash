// expect: 1
import Ashes.Result
match Result.map(fun (x) -> x + 1)(Error(1)) with
    | Ok(_) -> Ashes.IO.print(0)
    | Error(e) -> Ashes.IO.print(e)
