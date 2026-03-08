// expect-compile-error: Occurs check failed
let f = 
    fun (x) -> x(x)
in Ashes.IO.print(0)
