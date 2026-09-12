// expect: 1234
import Ashes.Collection.List
let digits =
    given (acc) ->
        given (x) -> acc * 10 + x
in
    [3, 4]
    |> List.append([1, 2])
    |> List.fold(digits)(0)
    |> Ashes.IO.print
