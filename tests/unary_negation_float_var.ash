// Regression: unary minus `-x` desugars to `0 - x`; the synthesized 0 is an Int literal, so negating
// a non-literal Float/BigInt (a variable or a compound expression) was rejected as Int-vs-Float
// (ASH002). Literals were already folded; this covers variables and expressions. A literal 0 is the
// identity of every numeric type, so it is now coerced to the other operand's concrete numeric type.
// Int negation is unchanged. (n-body needed the `0.0 - x` workaround before this.)
// expect: -5.00 -6.00 2.00 -7 -42
import Ashes.IO as io
import Ashes.Text as text
import Ashes.BigInt as big
let total = 5.0

let f x = 0.0 - x

let b = big.fromInt(42)

io.print(text.formatFloat(-total)(2) + " " + text.formatFloat(-(total + 1.0))(2) + " " + text.formatFloat(-f(2.0))(2) + " " + text.fromInt(-7) + " " + text.fromBigInt(-b))
