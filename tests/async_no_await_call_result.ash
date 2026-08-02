// Regression: an async block that completes without suspending lowers its body inline, outside the
// coroutine-body placement context. A call inside it may take ownership of the callee's result only
// when the callee's compiled body really produced a reference-counted value -- the ownership summary
// alone is an AST-level fact and reports an RC-eligible result for a helper whose body compiled to a
// region value. Trusting it skipped the copy-out, so the result pointed into a reclaimed region and
// was released as if it carried a reference count, which overwrote the neighbouring string's length
// word: "A7abcdefgh!" came back as "A7abcdefg!".
// expect: A7abcdefgh!/A7abcdefgh!?/7abcdefgh!?
import Ashes.IO
import Ashes.Task
import Ashes.Text
let build n = Ashes.Text.fromInt(n) + "abcdefgh"

let three n =
    async(let made = build(n)
    in "A" + made + "!")

let four n =
    async(let made = build(n)
    in "A" + made + "!" + "?")

let tail n =
    async(let made = build(n)
    in made + "!" + "?")

let render task =
    match Ashes.Task.run(task) with
        | Ok(v) -> v
        | Error(_e) -> "err"

Ashes.IO.print(render(three(7)) + "/" + render(four(7)) + "/" + render(tail(7)))
