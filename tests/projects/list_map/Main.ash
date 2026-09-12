// expect: 234
import Ashes.Collection.List
let inc =
    given (x) -> x + 1
in
    let digits =
        given (acc) ->
            given (x) -> acc * 10 + x
    in
        [1, 2, 3]
        |> List.map(inc)
        |> List.fold(digits)(0)
        |> Ashes.IO.print
