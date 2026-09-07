// Milestone 7 acceptance: a spawned task holding owned heap values is reaped after the driver stops
// waiting on it. The frame owns a string, a list, an ADT, owned Bytes and a closure over the string,
// so a reap that fails to release them leaks and one that releases them twice corrupts the free list.
// expect: spawned=ok
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
            let boxed =
                3
                |> build
                |> Boxed
            in
                let raw =
                    4
                    |> build
                    |> Ashes.Byte.fromText
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

let spawned =
    match (let _handle =
        0
        |> holder("detached")
        |> Ashes.Task.spawn
    in
        match await Ashes.Task.sleep(5) with
            | Ok(_u) -> "ok"
            | Error(_e) -> "err")
    |> async
    |> Ashes.Task.run with
        | Ok(v) -> v
        | Error(_e) -> "err"

Ashes.IO.print("spawned=" + spawned)
