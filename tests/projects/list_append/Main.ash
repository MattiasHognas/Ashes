// expect: 1234
import Ashes.List
let digits = 
    fun (acc) -> 
        fun (x) -> acc * 10 + x
in Ashes.IO.print(List.fold(digits)(0)(List.append([1, 2])([3, 4])))
