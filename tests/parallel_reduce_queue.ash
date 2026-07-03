// expect: z|solo!|a,b,c,d,e,f,g,h|4950|100
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let rec range lo hi = 
    if lo >= hi
    then []
    else lo :: range(lo + 1)(hi)

let join a b = a + "," + b

let empty = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> a + b)("z")(fun (x) -> x)([])

let one = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> a + b)("z")(fun (x) -> x + "!")(["solo"])

let ordered = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> join(a)(b))("")(fun (x) -> x)(["a", "b", "c", "d", "e", "f", "g", "h"])

let total = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> a + b)(0)(fun (x) -> x)(range(0)(100))

let counted = 
    Ashes.Parallel.reduce(fun (a) -> 
        fun (b) -> a + b)(0)(fun (_x) -> 1)(range(0)(100))
in Ashes.IO.print(empty + "|" + one + "|" + ordered + "|" + Ashes.Text.fromInt(total) + "|" + Ashes.Text.fromInt(counted))
