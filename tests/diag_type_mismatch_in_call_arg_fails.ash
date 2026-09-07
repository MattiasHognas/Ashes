let add =
    given (x) ->
        given (y) -> x + y
in
    "x"
    |> add(1)
    |> Ashes.IO.print
