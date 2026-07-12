// Regression: an accumulator whose type is still an unresolved inference variable when the TCO
// back-edge lowers (here: the stable `[] -> acc` leaf lowers first, so nothing has pinned acc to
// Str yet) no longer silently loses its arena reset. The back-edge emits a TcoResetPending
// placeholder and ResolveDeferredTcoResets generates the real block at the end of lowering, once
// the deferred '+' resolution has grounded the types — 60k iterations ran at 1.76 GB before, 0.25 MB
// after (verified manually; this guards correctness on both the optimized and unoptimized pipelines,
// and the test runner compiles unoptimized IR — exactly where a wrong splice would miscompile).
// expect: 6100
import Ashes.IO as io
import Ashes.Text as text
let recursive buildL table i acc =
    if i <= 0
    then acc
    else
        match table with
            | h :: _ -> buildL(table)(i - 1)(acc + h)
            | [] -> acc

io.print(text.fromInt(text.byteLength(buildL(["ab"])(3050)(""))))
