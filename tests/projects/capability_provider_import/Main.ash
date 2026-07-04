// expect: 1|3
import Ordering
let localMin : a -> List(a) -> a needs {Ord(a)} = 
    given (seed) -> 
        given (xs) -> 
            match xs with
                | [] -> seed
                | h :: t -> 
                    if Ord.compare(h)(seed) < 0
                    then h
                    else seed

Ashes.IO.print(Ashes.Text.fromInt(Ordering.minOf(100)([5, 3, 9, 1, 7])) + "|" + Ashes.Text.fromInt(localMin(3)([8, 6])))
