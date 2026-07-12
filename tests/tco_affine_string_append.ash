// Regression: affine in-place string growth (ConcatStrTip). An accumulator param consumed exactly
// once along every loop-continuing path (the affine analysis), only as the leftmost leaf of its own
// tail-call `+` chain, is uniquely owned above the loop-entry watermark — so the append extends it
// IN PLACE at the arena tip (V1), absorbs a freshly allocated right operand (V2, e.g. a closure
// call's result), and keeps extending across 4 MiB chunk boundaries (headroom chunks + watermark
// rebase via the chunk footer). Content and length are both checked; `big` crosses the chunk size.
// The test runner compiles unoptimized IR, covering the deferred-add (late-typed) arming too.
// expect: ababababab 6000000 300000 a3x7a2x6a1x5
import Ashes.IO as io
import Ashes.Text as text
let recursive small i acc =
    if i <= 0
    then acc
    else small(i - 1)(acc + "ab")

let recursive big i acc =
    if i <= 0
    then acc
    else big(i - 1)(acc + "xy")

let recursive viaCall f i acc =
    if i <= 0
    then acc
    else viaCall(f)(i - 1)(acc + f(i))

let recursive chain i acc =
    if i <= 0
    then acc
    else chain(i - 1)(acc + "a" + text.fromInt(i % 10) + "x" + text.fromInt(i % 10 + 4))

let f n = text.fromInt(n % 10)

io.print(small(5)("") + " " + text.fromInt(text.byteLength(big(3000000)(""))) + " " + text.fromInt(text.byteLength(viaCall(f)(300000)(""))) + " " + chain(3)(""))
