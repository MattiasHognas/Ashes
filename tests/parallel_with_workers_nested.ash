// expect: 9
import Ashes.Task.Parallel
import Ashes.IO
let plus =
    given (a) ->
        given (b) -> a + b

let inc =
    given (x) -> x + 1

let mapped =
    Ashes.Task.Parallel.withWorkers(8)(given (_u) ->
        Ashes.Task.Parallel.withWorkers(2)(given (_v) -> Ashes.Task.Parallel.map(inc)([1, 2, 3])))

Ashes.IO.print(Ashes.Task.Parallel.reduce(plus)(0)(given (x) -> x)(mapped))
