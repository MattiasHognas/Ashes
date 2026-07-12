// expect: 55
import Ashes.Parallel
import Ashes.IO
let plus =
    given (a) ->
        given (b) -> a + b

let sq =
    given (x) -> x * x

Ashes.IO.print(Ashes.Parallel.withWorkers(2)(given (_u) -> Ashes.Parallel.reduce(plus)(0)(sq)([1, 2, 3, 4, 5])))
