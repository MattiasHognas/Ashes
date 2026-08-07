// Milestone 7 acceptance: a race and a spawn in one program, both holding owned heap values, plus a
// third scheduler run after them. A run returns as soon as its main task completes, so the race
// loser and the spawned task are still linked in the scheduler's queues when it does; a later run
// that steps one of them reads a task whose storage the previous run already reclaimed. Each holder
// owns a string, a list, an ADT, owned Bytes and a closure, so the abandoned frames also have
// something to release.
// expect: race=fast|spawn=ok|after=done
import Ashes.IO
import Ashes.Task
import Ashes.Text
import Ashes.Byte
import Ashes.Collection.List
type Box =
    | Boxed(Str)

let build n = Ashes.Text.fromInt(n) + "-tail"

let holder label delayMs =
    async(let text = build(7)
    in
        let items = [build(1), build(2)]
        in
            let boxed = Boxed(build(3))
            in
                let raw = Ashes.Byte.fromText(build(4))
                in
                    let render =
                        given (suffix) -> text + suffix
                    in
                        match await Ashes.Task.sleep(delayMs) with
                            | Ok(_u) ->
                                match boxed with
                                    | Boxed(inner) ->
                                        if Ashes.Text.byteLength(render("!")) + Ashes.Collection.List.length(items) + Ashes.Byte.length(raw) + Ashes.Text.byteLength(inner) > 0
                                        then label
                                        else "short"
                            | Error(_e) -> "err")

let raced =
    match Ashes.Task.run(Ashes.Task.race([holder("fast")(0), holder("slow")(60)])) with
        | Ok(v) -> v
        | Error(_e) -> "err"

let spawned =
    match Ashes.Task.run(async(let _handle = Ashes.Task.spawn(holder("detached")(0))
    in
        match await Ashes.Task.sleep(5) with
            | Ok(_u) -> "ok"
            | Error(_e) -> "err")) with
        | Ok(v) -> v
        | Error(_e) -> "err"

let after =
    match Ashes.Task.run(async(match await Ashes.Task.sleep(80) with
        | Ok(_u) -> "done"
        | Error(_e) -> "err")) with
        | Ok(v) -> v
        | Error(_e) -> "err"

Ashes.IO.print("race=" + raced + "|spawn=" + spawned + "|after=" + after)
