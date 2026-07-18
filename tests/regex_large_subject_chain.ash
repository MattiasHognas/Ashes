// Regression: chaining Ashes.Text.Regex.replace on a subject larger than one heap chunk (4 MiB) used to
// allocate ~28 GB and get OOM-killed. The substitute output buffer (2*subject + 256) exceeded a
// single fixed-size chunk, so the ensure-space loop grew one 4 MiB chunk per iteration forever and
// never fit the request. Heap chunks now grow to fit an oversized allocation. The subject here is
// ~5 MiB (larger than one chunk); the chain must complete in bounded memory with the right length.
// expect: 5242880
import Ashes.Text.Regex as regex
import Ashes.IO as io
import Ashes.Text as text
let recursive grow s n =
    if n == 0
    then s
    else grow(s + s)(n - 1)

let seq = grow("acgtWacgtWacgtWacgtW")(18)

let s1 =
    match regex.compile("W") with
        | Ok(rx) -> regex.replace(rx)(seq)("Z")
        | Error(e) -> seq

let s2 =
    match regex.compile("Q") with
        | Ok(rx) -> regex.replace(rx)(s1)("X")
        | Error(e) -> s1
in io.print(text.fromInt(text.byteLength(s2)))
