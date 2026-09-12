// expect: 6
import Ashes.Collection.List
let add =
    given (acc) ->
        given (x) -> acc + x
in
    [1, 2, 3]
    |> List.fold(add)(0)
    |> Ashes.IO.print
