import Ashes.IO
let add = 
    fun (x) -> 
        fun (y) -> x + y
in print(add(10)(20))
