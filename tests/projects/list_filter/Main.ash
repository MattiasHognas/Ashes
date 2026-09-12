// expect: 34
import Ashes.Collection.List
let keep =
    given (x) -> x >= 3
in
    let digits =
        given (acc) ->
            given (x) -> acc * 10 + x
    in
        [1, 2, 3, 4]
        |> List.filter(keep)
        |> List.fold(digits)(0)
        |> Ashes.IO.print
