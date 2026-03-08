let add = 
    fun (x) -> 
        fun (y) -> x + y
in Ashes.IO.print(add(1)(2))
