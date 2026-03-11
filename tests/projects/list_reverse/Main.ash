// expect: 321
import Ashes.List
let digits = 
    fun (acc) -> 
        fun (x) -> acc * 10 + x
in Ashes.IO.print(List.fold(digits)(0)(List.reverse([1, 2, 3])))
