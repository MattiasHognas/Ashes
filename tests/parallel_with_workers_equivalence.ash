// expect: 4950|4950
import Ashes.Task.Parallel
import Ashes.Text
import Ashes.IO
let recursive range lo hi =
    if lo >= hi
    then []
    else lo :: range(lo + 1)(hi)

let plus =
    given (a) ->
        given (b) -> a + b

let id =
    given (x) -> x

let scoped =
    Ashes.Task.Parallel.withWorkers(1)(given (_u) -> Ashes.Task.Parallel.reduce(plus)(0)(id)(range(0)(100)))

let plain = Ashes.Task.Parallel.reduce(plus)(0)(id)(range(0)(100))

Ashes.IO.print(Ashes.Text.fromInt(scoped) + "|" + Ashes.Text.fromInt(plain))
