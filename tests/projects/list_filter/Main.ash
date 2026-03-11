// expect: 34
import Ashes.List
let keep = 
    fun (x) -> x >= 3
in 
    let digits = 
        fun (acc) -> 
            fun (x) -> acc * 10 + x
    in Ashes.IO.print(List.fold(digits)(0)(List.filter(keep)([1, 2, 3, 4])))
