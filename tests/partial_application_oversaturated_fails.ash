// expect-compile-error: Call to 'add' expects 2 argument(s) but got 3.
let add : Int -> Int -> Int =
    given (x) ->
        given (y) -> x + y
in
    3
    |> add(1)(2)
    |> Ashes.IO.print
