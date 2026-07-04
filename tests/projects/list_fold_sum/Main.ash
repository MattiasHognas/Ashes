// expect: 6
import Ashes.List
let add = 
    given (acc) -> 
        given (x) -> acc + x
in Ashes.IO.print(List.fold(add)(0)([1, 2, 3]))
