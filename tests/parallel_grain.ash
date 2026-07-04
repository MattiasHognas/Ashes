// expect: 2,4,6,8,10,| 15 | 2,4,6,8,10,| 15
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let nums = 1 :: 2 :: 3 :: 4 :: 5 :: []

let recursive showList xs = 
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showList(t)

let doubledCoarse = 
    Ashes.Parallel.mapGrained(16)(given (x) -> x * 2)(nums)

let doubledDefault = 
    Ashes.Parallel.map(given (x) -> x * 2)(nums)

let sumCoarse = 
    Ashes.Parallel.reduceGrained(16)(given (a) -> 
        given (b) -> a + b)(0)(given (x) -> x)(nums)

let sumDefault = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)(0)(given (x) -> x)(nums)
in Ashes.IO.print(showList(doubledCoarse) + "| " + Ashes.Text.fromInt(sumCoarse) + " | " + showList(doubledDefault) + "| " + Ashes.Text.fromInt(sumDefault))
