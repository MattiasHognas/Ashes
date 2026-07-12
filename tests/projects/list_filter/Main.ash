// expect: 34
import Ashes.List
let keep =
    given (x) -> x >= 3
in
    let digits =
        given (acc) ->
            given (x) -> acc * 10 + x
    in Ashes.IO.print(List.fold(digits)(0)(List.filter(keep)([1, 2, 3, 4])))
