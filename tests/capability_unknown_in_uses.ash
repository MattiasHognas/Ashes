// expect-compile-error: Unknown capability 'Nope'

let f : Int -> Int needs {Nope} = 
    given (x) -> x

Ashes.IO.print(f(1))
