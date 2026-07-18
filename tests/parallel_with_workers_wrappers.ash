// expect: 55|36|36
import Ashes.Task.Parallel
import Ashes.Text
import Ashes.IO
let plus =
    given (a) ->
        given (b) -> a + b

let sq =
    given (x) -> x * x

let r = Ashes.Task.Parallel.reduceWithWorkers(2)(plus)(0)(sq)([1, 2, 3, 4, 5])

let rg =
    Ashes.Task.Parallel.reduceGrainedWithWorkers(2)(2)(plus)(0)(given (x) -> x)([1, 2, 3, 4, 5, 6, 7, 8])

let m =
    Ashes.Task.Parallel.mapWithWorkers(3)(given (x) -> x + 10)([1, 2, 3])

Ashes.IO.print(Ashes.Text.fromInt(r) + "|" + Ashes.Text.fromInt(rg) + "|" + Ashes.Text.fromInt(Ashes.Task.Parallel.reduce(plus)(0)(given (x) -> x)(m)))
