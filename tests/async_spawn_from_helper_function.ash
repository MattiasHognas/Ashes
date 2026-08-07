// A side-effecting intrinsic's Unit result, bound inside a coroutine body. Unit is a nullary
// constructor and therefore an ordinary reference-counted heap cell, so a caller may own and release
// it -- but these intrinsics used to hand back the instruction's raw status word instead of a real
// Unit, and releasing that reads a reference count sixteen bytes below a null pointer. Reached here
// by calling spawn through a plain helper, so its result becomes an ordinary call result the
// enclosing coroutine owns rather than a value consumed on the spot.
// expect: spawned=ok|stopped=ok
import Ashes.IO
import Ashes.Task
let leaf k =
    async(match await Ashes.Task.sleep(0) with
        | Ok(_u) -> k + 1
        | Error(_e) -> -1)

let spawnThrough k = Ashes.Task.spawn(leaf(k))

let spawned =
    match Ashes.Task.run(async(let _handle = spawnThrough(7)
    in
        match await Ashes.Task.sleep(5) with
            | Ok(_u) -> "ok"
            | Error(_e) -> "err")) with
        | Ok(v) -> v
        | Error(_e) -> "err"

let stopped =
    match Ashes.Task.run(async(let _first = spawnThrough(1)
    in
        let _second = spawnThrough(2)
        in
            match await Ashes.Task.sleep(5) with
                | Ok(_u) -> "ok"
                | Error(_e) -> "err")) with
        | Ok(v) -> v
        | Error(_e) -> "err"

Ashes.IO.print("spawned=" + spawned + "|stopped=" + stopped)
