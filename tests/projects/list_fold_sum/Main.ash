// expect: 6
import List
let add = 
    fun (acc) -> 
        fun (x) -> acc + x
in Ashes.IO.print(List.fold(add)(0)([1, 2, 3]))
