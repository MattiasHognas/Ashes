// Edge cases for the freestanding SWAR Byte.indexOf scan (static images; TLS images use libc
// memchr instead): hits in the unaligned prologue, on word boundaries, in the scalar tail, a
// zero needle, 0x80-heavy payloads (the SWAR zero-byte mask territory), absent needles, and
// scans over an unaligned subView base.
// expect: 0 7 8 15 16 20 -1 -1 3 5 3 64 -1
import Ashes.Byte as bytes
import Ashes.Collection.List as list
import Ashes.IO as io
import Ashes.Number.UInt as uint
import Ashes.Text as text
let recursive buildRun value count acc =
    if count <= 0
    then acc
    else buildRun(value)(count - 1)(bytes.appendByte(acc)(uint.fromInt(value)))

let payload = bytes.appendByte(buildRun(65)(20)(bytes.empty(Unit)))(uint.fromInt(59))

let hit at = bytes.indexOf(payload)(65)(at)

let semicolonAt = bytes.indexOf(payload)(59)(0)

let zeroes = bytes.appendByte(buildRun(128)(3)(bytes.empty(Unit)))(uint.fromInt(0))

let zeroHit = bytes.indexOf(zeroes)(0)(0)

let highHit = bytes.indexOf(bytes.appendByte(buildRun(127)(5)(bytes.empty(Unit)))(uint.fromInt(128)))(128)(0)

let viewBase = bytes.fromText("xxAbcdefgh")

let unalignedView = bytes.fromText(bytes.subView(viewBase)(2)(8))

let viewHit = bytes.indexOf(unalignedView)(100)(0)

let recursive buildLong count acc =
    if count <= 0
    then bytes.appendByte(acc)(uint.fromInt(33))
    else buildLong(count - 1)(bytes.appendByte(acc)(uint.fromInt(46)))

let longScan = bytes.indexOf(buildLong(64)(bytes.empty(Unit)))(33)(0)

let results = [hit(0), hit(7), hit(8), hit(15), hit(16), semicolonAt, bytes.indexOf(payload)(90)(0), bytes.indexOf(payload)(65)(21), zeroHit, highHit, viewHit, longScan, bytes.indexOf(bytes.empty(Unit))(65)(0)]

let recursive render items acc =
    match items with
        | [] -> acc
        | value :: rest ->
            render(rest)(if acc == ""
            then text.fromInt(value)
            else acc + " " + text.fromInt(value))

io.print(render(results)(""))
