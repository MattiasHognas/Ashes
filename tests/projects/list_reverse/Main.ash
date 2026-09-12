// expect: 321
import Ashes.Collection.List
let digits =
    given (acc) ->
        given (x) -> acc * 10 + x
in
    [1, 2, 3]
    |> List.reverse
    |> List.fold(digits)(0)
    |> Ashes.IO.print
