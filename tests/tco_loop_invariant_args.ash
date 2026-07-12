// Regression: a loop-invariant heap argument (a param passed as its own unchanged Var at every
// tail self-call — here a CLOSURE and a LIST) no longer disqualifies the growing accumulator's
// fixed-watermark reset. It lives below the loop-entry watermark, needs no copy-out, and is exempt
// from the fixed-mark qualification — so fasta's randomFasta shape (closure table + growing Str)
// runs in constant memory instead of stranding every iteration's accumulator copy (27 GB at
// N=40000 before). Verified constant (3.8 MB) manually; this guards correctness on both pipelines.
// expect: 11590 24200
import Ashes.IO as io
import Ashes.Text as text
let recursive buildC f i acc =
    if i <= 0
    then acc + ""
    else buildC(f)(i - 1)(acc + f(i))

let recursive buildL table i acc =
    if i <= 0
    then acc + ""
    else
        match table with
            | h :: _ -> buildL(table)(i - 1)(acc + h + h)
            | [] -> acc

let f n = text.fromInt(n % 100)

io.print(text.fromInt(text.byteLength(buildC(f)(6100)(""))) + " " + text.fromInt(text.byteLength(buildL(["xy", "z"])(6050)(""))))
