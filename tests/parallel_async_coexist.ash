// expect: 499999500000|slept|499999500000
// Structured-parallelism (`both`) + the async runtime coexist and stay correct.
// Two genuinely-forking `both`s (concrete Int result, so they spawn a worker)
// run before and after a synchronous `Ashes.Async.run` await; every result must
// match the sequential value. Portable (no loopback server), so it guards
// fork-runtime / async-runtime coexistence on every target. (A `both` worker
// running CONCURRENTLY with a live async I/O is covered by
// `parallel_async_overlap.ash`.)
import Ashes.Parallel
import Ashes.Text
import Ashes.IO
import Ashes.Async
let recursive sumRange lo hi acc = 
    if lo >= hi
    then acc
    else sumRange(lo + 1)(hi)(acc + lo)

let halves = 
    given (u) -> 
        match Ashes.Parallel.both(given (v) -> sumRange(0)(500000)(0))(given (v) -> sumRange(500000)(1000000)(0)) with
            | (a, b) -> a + b

let before = halves(0)

let slept = 
    match Ashes.Async.run(async(let ok = await Ashes.Async.sleep(1)
    in "slept")) with
        | Ok(text) -> text
        | Error(err) -> err

let after = halves(0)
in Ashes.IO.print(Ashes.Text.fromInt(before) + "|" + slept + "|" + Ashes.Text.fromInt(after))
