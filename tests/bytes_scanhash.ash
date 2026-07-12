// expect: idx=7 hash-ok missing-ok
import Ashes.IO
import Ashes.Bytes
import Ashes.Text
let b = Ashes.Bytes.fromText("Hamburg;12.0")

let r =
    match Ashes.Bytes.scanHash(b)(59)(0) with
        | (idx, h) ->
            let want = Ashes.Bytes.hash(Ashes.Bytes.fromText("Hamburg"))
            in
                if h == want
                then "idx=" + Ashes.Text.fromInt(idx) + " hash-ok"
                else "idx=" + Ashes.Text.fromInt(idx) + " HASH-MISMATCH"

let r2 =
    match Ashes.Bytes.scanHash(b)(88)(0) with
        | (idx, h) ->
            if idx == -1
            then
                if h == Ashes.Bytes.hash(b)
                then " missing-ok"
                else " missing-HASH-BAD"
            else " missing-IDX-BAD"

Ashes.IO.writeLine(r + r2)
