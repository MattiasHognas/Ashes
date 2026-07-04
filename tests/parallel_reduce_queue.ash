// expect: z|solo!|a,b,c,d,e,f,g,h|0,1,2,3,4,5,6,7,8,9,10,11,12|4950|100
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
let recursive range lo hi = 
    if lo >= hi
    then []
    else lo :: range(lo + 1)(hi)

let join a b = a + "," + b

let empty = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)("z")(given (x) -> x)([])

let one = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)("z")(given (x) -> x + "!")(["solo"])

let ordered = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> join(a)(b))("")(given (x) -> x)(["a", "b", "c", "d", "e", "f", "g", "h"])

let orderedOdd = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> join(a)(b))("")(given (x) -> Ashes.Text.fromInt(x))(range(0)(13))

let total = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)(0)(given (x) -> x)(range(0)(100))

let counted = 
    Ashes.Parallel.reduce(given (a) -> 
        given (b) -> a + b)(0)(given (_x) -> 1)(range(0)(100))
in Ashes.IO.print(empty + "|" + one + "|" + ordered + "|" + orderedOdd + "|" + Ashes.Text.fromInt(total) + "|" + Ashes.Text.fromInt(counted))
