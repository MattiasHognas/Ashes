// expect-compile-error: Occurs check failed (recursive type).
let rec f = 
    fun (x) -> f([x])
in Ashes.IO.print(0)
