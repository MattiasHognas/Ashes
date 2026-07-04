// Generic capabilities via dictionary passing: a function annotated `needs {Ord(a)}` works over
// any element type, and the provider is threaded in at the concrete call site — no handler.
import Ashes.IO
import Ashes.List
import Ashes.Text
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare = 
        given (x) -> 
            given (y) -> x - y

let minOf : a -> List(a) -> a needs {Ord(a)} = 
    given (first) -> 
        given (items) -> 
            Ashes.List.foldLeft(given (best) -> 
                given (next) -> 
                    if Ord.compare(next)(best) < 0
                    then next
                    else best)(first)(items)

Ashes.IO.print(Ashes.Text.fromInt(minOf(1000)([5, 3, 9, 1, 7])))
