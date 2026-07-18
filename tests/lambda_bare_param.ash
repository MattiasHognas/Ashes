// Regression: a single-parameter `given` lambda no longer requires parentheses — `given x -> body`
// parses the same as `given (x) -> body`. The parenthesized (and multi-parameter) form still works.
// fmt-skip: the canonical formatter re-parenthesizes `given n ->` to `given (n) ->`; the bare form
// is kept verbatim here to guard the parser accepting it.
// expect: 42 5
import Ashes.IO as io
import Ashes.Text as text
let apply f x = f(x)

let inc =
    given (n) -> n + 1

let addp =
    given (a) ->
        given (b) -> a + b
in io.print(text.fromInt(apply(inc)(41)) + " " + text.fromInt(addp(2)(3)))
