// expect-compile-error: Unknown capability 'Nope'

let f : Int -> Int needs {Nope} =
    given (x) -> x

1
|> f
|> Ashes.IO.print
