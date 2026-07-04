// expect-compile-error: Unknown effect 'Nope'

let f : Int -> Int uses {Nope} = 
    given (x) -> x

Ashes.IO.print(f(1))
