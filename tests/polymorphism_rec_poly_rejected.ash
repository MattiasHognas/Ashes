// expect-compile-error: Occurs check failed (recursive type).
let recursive f = 
    given (x) -> f([x])
in Ashes.IO.print(0)
