// Milestone 7 acceptance: a spawned task holding owned heap values is reaped after the driver stops
// waiting on it. The frame owns a string, a list and an ADT, so a reap that fails to release them
// leaks and one that releases them twice corrupts the free list.
// expect: spawned=ok
import Ashes.IO
import Ashes.Task
import Ashes.Text
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
                match await Ashes.Task.sleep(delayMs) with
                    | Ok(_u) ->
                        match boxed with
                            | Boxed(inner) ->
                                if Ashes.Text.byteLength(text) + Ashes.Collection.List.length(items) + Ashes.Text.byteLength(inner) > 0
                                then label
                                else "short"
                    | Error(_e) -> "err")

let spawned =
    match Ashes.Task.run(async(let _handle = Ashes.Task.spawn(holder("detached")(0))
    in
        match await Ashes.Task.sleep(5) with
            | Ok(_u) -> "ok"
            | Error(_e) -> "err")) with
        | Ok(v) -> v
        | Error(_e) -> "err"

Ashes.IO.print("spawned=" + spawned)
