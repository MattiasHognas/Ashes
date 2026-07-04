// expect-compile-error: Occurs check failed
let f = 
    given (x) -> x(x)
in Ashes.IO.print(0)
