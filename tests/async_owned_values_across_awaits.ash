// Milestone 7 acceptance: ordinary owned heap values of every supported shape held across suspension
// points. Each value is built before the first await, read after it, read again after a second one,
// and the last is returned as the task's result -- so a missed release leaks, a premature release
// reads freed memory, and a double release corrupts the free list. Region and reference-counted
// representations both flow through here, since the body's placement is now ordinary while the
// result crosses the task boundary.
// expect: str=7-tail!|list=3|adt=Boxed(7-tail)|tuple=(7-tail,2)|bytes=6|closure=8
import Ashes.IO
import Ashes.Task
import Ashes.Text
import Ashes.Byte
import Ashes.Collection.List
type Box =
    | Boxed(Str)

let build n = Ashes.Text.fromInt(n) + "-tail"

let showBox b =
    match b with
        | Boxed(text) -> "Boxed(" + text + ")"

let strTask n =
    async(let made = build(n)
    in
        match await Ashes.Task.sleep(0) with
            | Ok(_u) ->
                match await Ashes.Task.sleep(0) with
                    | Ok(_v) -> made + "!"
                    | Error(_e) -> "err"
            | Error(_e2) -> "err")

let listTask n =
    async(let made = [build(n), build(n + 1), build(n + 2)]
    in
        match await Ashes.Task.sleep(0) with
            | Ok(_u) -> Ashes.Collection.List.length(made)
            | Error(_e) -> -1)

let adtTask n =
    async(let made = Boxed(build(n))
    in
        match await Ashes.Task.sleep(0) with
            | Ok(_u) -> showBox(made)
            | Error(_e) -> "err")

let tupleTask n =
    async(let made = (build(n), 2)
    in
        match await Ashes.Task.sleep(0) with
            | Ok(_u) ->
                match made with
                    | (text, count) -> "(" + text + "," + Ashes.Text.fromInt(count) + ")"
            | Error(_e) -> "err")

let bytesTask n =
    async(let made = Ashes.Byte.fromText(build(n))
    in
        match await Ashes.Task.sleep(0) with
            | Ok(_u) -> Ashes.Byte.length(made)
            | Error(_e) -> -1)

let closureTask n =
    async(let made = build(n)
    in
        let render =
            given (suffix) -> made + suffix
        in
            match await Ashes.Task.sleep(0) with
                | Ok(_u) -> Ashes.Text.byteLength(render("!!"))
                | Error(_e) -> -1)

let render task fallback =
    match Ashes.Task.run(task) with
        | Ok(v) -> v
        | Error(_e) -> fallback

let renderInt task =
    match Ashes.Task.run(task) with
        | Ok(v) -> Ashes.Text.fromInt(v)
        | Error(_e) -> "err"

Ashes.IO.print("str=" + render(strTask(7))("err") + "|list=" + renderInt(listTask(7)) + "|adt=" + render(adtTask(7))("err") + "|tuple=" + render(tupleTask(7))("err") + "|bytes=" + renderInt(bytesTask(7)) + "|closure=" + renderInt(closureTask(7)))
