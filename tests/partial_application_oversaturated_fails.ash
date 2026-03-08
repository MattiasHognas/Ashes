// expect-compile-error: Call to 'add' expects 2 argument(s) but got 3.
let add = 
    fun (x) -> 
        fun (y) -> x + y
in Ashes.IO.print(add(1)(2)(3))
