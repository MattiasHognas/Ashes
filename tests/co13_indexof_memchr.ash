// expect: 0|2|12|-1|-1|5|11
// file: idxdata.txt = hello;world;x
// Exercises Bytes.indexOf across the memchr path: first byte, a mid byte, the last byte, a missing
// byte, a from-offset past the only match, and two delimiter scans (the 1BRC pattern).
import Ashes.IO
import Ashes.IO.File
import Ashes.Text
import Ashes.Byte
match Ashes.IO.File.readText("idxdata.txt") with
    | Error(_) -> Ashes.IO.print("err")
    | Ok(t) ->
        let b = Ashes.Byte.fromText(t)
        in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(104)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(108)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(120)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(122)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(104)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(59)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.indexOf(b)(59)(6)))
