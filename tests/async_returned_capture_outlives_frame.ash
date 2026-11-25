// A task that returns its captured runtime-managed string after an await hands the capture to its
// result, which is read after the task completes and its frame is torn down. The frame must not
// release the capture the result still holds.
// expect: 7-tail 692
import Ashes.IO
import Ashes.Task
import Ashes.Text
let build n = Ashes.Text.fromInt(n) + "-tail"

let runOnce n =
    (let text = build(n)
    in
        async(match await Ashes.Task.sleep(0) with
            | Ok(_u) -> text
            | Error(_e) -> "err"))

let recursive drain i acc =
    if i <= 0
    then acc
    else
        match i
        |> runOnce
        |> Ashes.Task.run with
            | Ok(t) -> drain(i - 1)(acc + Ashes.Text.length(t))
            | Error(_e) -> -1

let kept =
    match 7
    |> runOnce
    |> Ashes.Task.run with
        | Ok(t) -> t
        | Error(_e) -> "err"

Ashes.IO.print(kept + " " + Ashes.Text.fromInt(drain(100)(0)))
