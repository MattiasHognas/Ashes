let add = 
    given (x) -> 
        given (y) -> x + y
in Ashes.IO.print(add(1)("x"))
