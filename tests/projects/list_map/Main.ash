// expect: 234
import List
let inc = 
    fun (x) -> x + 1
in 
    let digits = 
        fun (acc) -> 
            fun (x) -> acc * 10 + x
    in Ashes.IO.print(List.fold(digits)(0)(List.map(inc)([1, 2, 3])))
