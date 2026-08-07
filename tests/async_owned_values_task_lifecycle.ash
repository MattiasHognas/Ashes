// Milestone 7 acceptance: the terminal paths a task frame can take while holding owned heap values.
// A race loser is cancelled while parked and a task built but never run is abandoned outright. Each
// holds a string, a list, an ADT, owned Bytes and a closure over the string, so a frame that fails
// to release them leaks and one that releases them twice corrupts the free list. The winner's value
// is still read after the race resolves.
// expect: race=fast|abandoned=ok
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

let abandoned =
    (let _never = holder("never")(0)
    in "ok")

Ashes.IO.print("race=" + raced + "|abandoned=" + abandoned)
