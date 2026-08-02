// Milestone 7 frame teardown: a captured runtime-managed value is owned by the task frame, read on
// both sides of an await, and released exactly once when the task completes. Repeating the task
// many times must not grow the heap -- a missing release leaks per iteration and a double release
// corrupts the free list, so a wrong answer or a crash here is the regression signal.
// expect: 7-tail!/7-tail!/done 200
import Ashes.IO
import Ashes.Task
import Ashes.Text
let build n = Ashes.Text.fromInt(n) + "-tail"

let runOnce n =
    (let text = build(n)
    in
        async(match await Ashes.Task.sleep(0) with
            | Ok(_u) -> text + "!"
            | Error(_e) -> "err"))

let recursive drain i =
    if i <= 0
    then i
    else
        match Ashes.Task.run(runOnce(7)) with
            | Ok(_t) -> drain(i - 1)
            | Error(_e) -> -1

let first =
    match Ashes.Task.run(runOnce(7)) with
        | Ok(t) -> t
        | Error(_e) -> "err"

let second =
    match Ashes.Task.run(runOnce(7)) with
        | Ok(t) -> t
        | Error(_e) -> "err"

Ashes.IO.print(first + "/" + second + "/done " + Ashes.Text.fromInt(200 + drain(200)))
