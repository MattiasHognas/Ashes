// A captured runtime-managed string the task frame retains is read after an await and released by
// the frame when the task completes. The task's result is an integer, so it cannot share the
// capture; repeating the task must neither leak the capture nor release it twice.
// expect: 692
import Ashes.IO
import Ashes.Task
import Ashes.Text
let build n = Ashes.Text.fromInt(n) + "-tail"

let measure n =
    (let text = build(n)
    in
        async(match await Ashes.Task.sleep(0) with
            | Ok(_u) -> Ashes.Text.length(text)
            | Error(_e) -> -1))

let recursive drain i acc =
    if i <= 0
    then acc
    else
        match i
        |> measure
        |> Ashes.Task.run with
            | Ok(n) -> drain(i - 1)(acc + n)
            | Error(_e) -> -1

0
|> drain(100)
|> Ashes.Text.fromInt
|> Ashes.IO.print
