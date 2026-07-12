// expect: 110
import Ashes.Parallel
import Ashes.IO
let plus =
    given (a) ->
        given (b) -> a + b

let id =
    given (x) -> x

let a =
    Ashes.Parallel.withWorkers(1)(given (_u) -> Ashes.Parallel.reduce(plus)(0)(id)([1, 2, 3, 4]))

let b = Ashes.Parallel.reduce(plus)(0)(id)([10, 20, 30, 40])

Ashes.IO.print(a + b)
