// Regression: unary minus desugared to `0 - x`, and the synthesized Int `0` forced the result to
// resolve as Int -- so a negated float literal like `-0.5` was rejected as Int (ASH002), and it
// also cascaded (passing `-0.5` to a Float parameter poisoned that parameter to Int). A negated
// float literal now folds into the literal itself, so `-0.5` is a genuine Float. Int negation is
// unaffected (stays `0 - n`).
// expect: -0.5 3.5 -1.0 -5.0 5
import Ashes.IO as io
import Ashes.Text as text
let neg : Float -> Float = 
    given (x) -> 0.0 - x

let a = -0.5

let b = 3.0 - -0.5

let c = -0.5 * 2.0

io.write(text.fromFloat(a) + " " + text.fromFloat(b) + " " + text.fromFloat(c) + " " + text.fromFloat(neg(5.0)) + " " + text.fromInt(1 - -5 - 1) + "\n")
