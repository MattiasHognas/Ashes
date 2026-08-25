// Regression: the let-bound affine accumulator form `let acc2 = acc + rhs in loop(...)(acc2)` takes
// the same in-place ConcatStrTip path as the inline form. Move analysis follows the single-use let
// alias, the let value is armed, and the loaded binding carries the ConcatStrTip producer fact so
// the back edge skips the predecessor drop the append already consumed — without that skip the
// accumulator is freed while still live and this crashes once the reservation outgrows a chunk
// (historically between 2,000 and 3,000 iterations). `big` crosses that threshold by a wide margin;
// `content` checks exact bytes, and `chain` covers a multi-part armed let value.
// expect: 200000 09876543210987654321 30
import Ashes.IO as io
import Ashes.Text as text
let recursive big i acc =
    if i <= 0
    then acc
    else
        let acc2 = acc + "xy"
        in big(i - 1)(acc2)

let recursive content i acc =
    if i <= 0
    then acc
    else
        let acc2 = acc + text.fromInt(i % 10)
        in content(i - 1)(acc2)

let recursive chain i acc =
    if i <= 0
    then acc
    else
        let acc2 = acc + "a" + "b" + "c"
        in chain(i - 1)(acc2)

io.print(text.fromInt(text.byteLength(big(100000)(""))) + " " + content(20)("") + " " + text.fromInt(text.byteLength(chain(10)(""))))
