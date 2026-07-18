// Ashes.Task.Parallel is a submodule under the real module Ashes.Task; both import forms resolve.
// expect: 12 ok
import Ashes.Collection.List as list
import Ashes.IO as io
import Ashes.Task as task
import Ashes.Task.Parallel as parallel
import Ashes.Text as text
let doubled =
    parallel.map(given (x) -> x * 2)([1, 2, 3])

let summed =
    list.foldLeft(given (acc) ->
        given (x) -> acc + x)(0)(doubled)

let viaTask =
    match task.run(task.task(1)) with
        | Error(_e) -> "err"
        | Ok(_v) -> "ok"

io.print(text.fromInt(summed) + " " + viaTask)
