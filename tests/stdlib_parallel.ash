// expect: 2,4,6,8,| 30 | 42:hi
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let nums = 1 :: 2 :: 3 :: 4 :: []

let doubled = 
    Ashes.Parallel.map(fun (x) -> x * 2)(nums)

let sumSq = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> a + b)(0)(fun (x) -> x * x)(nums)

let pair = 
    Ashes.Parallel.both(fun (_u) -> 42)(fun (_u) -> "hi")

let rec showList xs = 
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showList(t)
in 
    match pair with
        | (a, b) -> Ashes.IO.print(showList(doubled) + "| " + Ashes.Text.fromInt(sumSq) + " | " + Ashes.Text.fromInt(a) + ":" + b)
