// expect: 15
// Async tail-recursive loop: a `let recursive` helper inside an async body whose body awaits
// compiles to ONE looping coroutine (suspend points + in-place restart), not nested blocking runs.
// The accumulator and counter must survive suspends and carry across the restart back-edge.
import Ashes.Async
import Ashes.IO
import Ashes.Text
let compute =
    async(let recursive go i acc =
        if i == 0
        then acc
        else
            match await Ashes.Async.sleep(1) with
                | Ok(_u) -> go(i - 1)(acc + i)
                | Error(_e) -> -1
    in go(5)(0))
in
    match Ashes.Async.run(compute) with
        | Ok(n) -> Ashes.IO.print(Ashes.Text.fromInt(n))
        | Error(_e2) -> Ashes.IO.print("err")
