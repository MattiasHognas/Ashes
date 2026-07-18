// expect: 4|9|27
// CO-18: Ashes.Task.Parallel.splitChunks(bytes)(sep)(n) splits a byte buffer into up to n contiguous
// (bytes, lo, hi) chunks at record-separator (here newline = 10) boundaries, so no record straddles
// a chunk. Fed straight to Ashes.Task.Parallel.reduce it keeps the parallel fold at the caller's concrete
// type. Here 9 lines each contain one 'z' (byte 122); counting 'z' per chunk and summing must total 9
// regardless of how the 9 lines fall across the 4 chunks, and the chunks must exactly tile the buffer
// (their spans, summed, equal the 27-byte buffer). Reported: chunk count | total z | covered bytes.
import Ashes.IO
import Ashes.Task.Parallel
import Ashes.Byte
import Ashes.Text
import Ashes.Number.UInt
let text = "az\nbz\ncz\ndz\nez\nfz\ngz\nhz\niz\n"

let bytes = Ashes.Byte.fromText(text)

let recursive countZ i hi acc =
    if i >= hi
    then acc
    else
        if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i)) == 122
        then countZ(i + 1)(hi)(acc + 1)
        else countZ(i + 1)(hi)(acc)

let foldZ triple =
    match triple with
        | (_b, lo, hi) -> countZ(lo)(hi)(0)

let foldSpan triple =
    match triple with
        | (_b, lo, hi) -> hi - lo

let recursive len xs acc =
    match xs with
        | [] -> acc
        | _h :: t -> len(t)(acc + 1)

let chunks = Ashes.Task.Parallel.splitChunks(bytes)(10)(4)

let addI a b = a + b

let nChunks = len(chunks)(0)

let totalZ = Ashes.Task.Parallel.reduce(addI)(0)(foldZ)(chunks)

let covered = Ashes.Task.Parallel.reduce(addI)(0)(foldSpan)(chunks)
in Ashes.IO.print(Ashes.Text.fromInt(nChunks) + "|" + Ashes.Text.fromInt(totalZ) + "|" + Ashes.Text.fromInt(covered))
