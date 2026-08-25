// Regression: a let-bound append whose binding has MORE than one use must NOT be treated as the
// in-place affine accumulator — arming it would let a second reader observe (or double-free) the
// in-place-grown value. Both shapes below keep the copying path and must stay correct: `exitUse`
// consumes the binding on the loop path and again as an exit value; `lenUse` reads its length
// before passing it on. Iteration counts are small because the (correct) declined path is O(n^2).
// expect: 2003 1500
import Ashes.IO as io
import Ashes.Text as text
let recursive exitUse i acc =
    if i <= 0
    then acc
    else
        let acc2 = acc + "x"
        in
            if i == 1
            then acc2 + "END"
            else exitUse(i - 1)(acc2)

let recursive lenUse i acc =
    if i <= 0
    then text.byteLength(acc)
    else
        let acc2 = acc + "x"
        in
            let len = text.byteLength(acc2)
            in
                if len == 0
                then -1
                else lenUse(i - 1)(acc2)

io.print(text.fromInt(text.byteLength(exitUse(2000)(""))) + " " + text.fromInt(lenUse(1500)("")))
