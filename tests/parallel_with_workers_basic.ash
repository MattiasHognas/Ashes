// expect: 55
import Ashes.Task.Parallel
import Ashes.IO
let plus =
    given (a) ->
        given (b) -> a + b

let sq =
    given (x) -> x * x

(given (_u) -> Ashes.Task.Parallel.reduce(plus)(0)(sq)([1, 2, 3, 4, 5]))
|> Ashes.Task.Parallel.withWorkers(2)
|> Ashes.IO.print
