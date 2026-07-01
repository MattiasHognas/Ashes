// expect: 2,4,6,8,10,| 15 | 2,4,6,8,10,| 15
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let nums = 1 :: 2 :: 3 :: 4 :: 5 :: []

let rec showList xs = 
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showList(t)

let doubledCoarse = 
    Ashes.Parallel.mapGrained(16)(fun (x) -> x * 2)(nums)

let doubledDefault = 
    Ashes.Parallel.map(fun (x) -> x * 2)(nums)

let sumCoarse = 
    Ashes.Parallel.reduceGrained(16)(fun (a) -> 
        fun (b) -> a + b)(0)(fun (x) -> x)(nums)

let sumDefault = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> a + b)(0)(fun (x) -> x)(nums)
in Ashes.IO.print(showList(doubledCoarse) + "| " + Ashes.Text.fromInt(sumCoarse) + " | " + showList(doubledDefault) + "| " + Ashes.Text.fromInt(sumDefault))
