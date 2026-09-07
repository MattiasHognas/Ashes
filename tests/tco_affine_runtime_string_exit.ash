// Regression: a TCO result can join the runtime-managed accumulator itself with a fresh
// concatenation derived from it. Late parameter promotion must normalize the concat arm to RC
// and ignore unreachable dummy join stores after tail jumps. Exceed 4 KiB so a bad drop unmaps it.
// expect: 6000 6001
import Ashes.IO as io
import Ashes.Text as text
let recursive build partial i output =
    if i <= 0
    then
        if partial
        then output + "z"
        else output
    else build(partial)(i - 1)(output + "xx")

io.print(text.fromInt(""
|> build(false)(3000)
|> text.byteLength) + " " + text.fromInt(""
|> build(true)(3000)
|> text.byteLength))
