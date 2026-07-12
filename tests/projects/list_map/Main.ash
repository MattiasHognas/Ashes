// expect: 234
import Ashes.List
let inc =
    given (x) -> x + 1
in
    let digits =
        given (acc) ->
            given (x) -> acc * 10 + x
    in Ashes.IO.print(List.fold(digits)(0)(List.map(inc)([1, 2, 3])))
