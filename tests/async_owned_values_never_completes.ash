// Milestone 7 acceptance: owned heap values abandoned inside a task that starts, suspends and never
// completes. The task is spawned, parks on a sleep far longer than the program's lifetime, and its
// frame still owns a string, a list, an ADT, owned Bytes and a closure when the process exits. A
// frame whose teardown only runs on completion therefore leaks every one of them, and one that
// releases eagerly frees values the parked frame still holds.
// expect: parked=ok
import Ashes.IO
import Ashes.Task
import Ashes.Text
import Ashes.Byte
import Ashes.Collection.List
type Box =
    | Boxed(Str)

let build n = Ashes.Text.fromInt(n) + "-tail"

let parked delayMs =
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
                                        Ashes.Text.byteLength(render("!")) + Ashes.Collection.List.length(items) + Ashes.Byte.length(raw) + Ashes.Text.byteLength(inner)
                            | Error(_e) -> -1)

let driver =
    match (let _spawned =
        600000
        |> parked
        |> Ashes.Task.spawn
    in
        match await Ashes.Task.sleep(5) with
            | Ok(_u) -> "ok"
            | Error(_e) -> "err")
    |> async
    |> Ashes.Task.run with
        | Ok(v) -> v
        | Error(_e) -> "err"

Ashes.IO.print("parked=" + driver)
