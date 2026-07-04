// expect: 9
import Ashes.Parallel
import Ashes.IO
let plus = 
    given (a) -> 
        given (b) -> a + b

let inc = 
    given (x) -> x + 1

let mapped = 
    Ashes.Parallel.withWorkers(8)(given (_u) -> 
        Ashes.Parallel.withWorkers(2)(given (_v) -> Ashes.Parallel.map(inc)([1, 2, 3])))

Ashes.IO.print(Ashes.Parallel.reduce(plus)(0)(given (x) -> x)(mapped))
