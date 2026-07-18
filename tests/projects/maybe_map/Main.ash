// expect: 2
import Ashes.Core.Maybe
let inc =
    given (x) -> x + 1
in
    match Maybe.map(inc)(Some(1)) with
        | Some(x) -> Ashes.IO.print(x)
        | None -> Ashes.IO.print(0)
