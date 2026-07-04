// expect: 1
import Ashes.List
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Int) =
    | compare = 
        given (a) -> 
            given (b) -> a - b

let minOf : a -> List(a) -> a needs {Ord(a)} = 
    given (first) -> 
        given (items) -> 
            Ashes.List.foldLeft(given (best) -> 
                given (next) -> 
                    if Ord.compare(next)(best) < 0
                    then next
                    else best)(first)(items)

Ashes.IO.print(minOf(100)([5, 3, 9, 1, 7]))
