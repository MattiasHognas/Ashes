// expect: -1 0 1 -1 1 0 1
import Ashes.IO
import Ashes.Bytes
let r1 = Ashes.Bytes.compare(Ashes.Bytes.fromText("abc"))(Ashes.Bytes.fromText("abd"))

let r2 = Ashes.Bytes.compare(Ashes.Bytes.fromText("abc"))(Ashes.Bytes.fromText("abc"))

let r3 = Ashes.Bytes.compare(Ashes.Bytes.fromText("abd"))(Ashes.Bytes.fromText("abc"))

let r4 = Ashes.Bytes.compare(Ashes.Bytes.fromText("ab"))(Ashes.Bytes.fromText("abc"))

let r5 = Ashes.Bytes.compare(Ashes.Bytes.fromText("abc"))(Ashes.Bytes.fromText("ab"))

let r6 = Ashes.Bytes.compare(Ashes.Bytes.fromText(""))(Ashes.Bytes.fromText(""))

let r7 = Ashes.Bytes.compare(Ashes.Bytes.fromText("b"))(Ashes.Bytes.fromText("a"))

Ashes.IO.writeLine(Ashes.Text.fromInt(r1) + " " + Ashes.Text.fromInt(r2) + " " + Ashes.Text.fromInt(r3) + " " + Ashes.Text.fromInt(r4) + " " + Ashes.Text.fromInt(r5) + " " + Ashes.Text.fromInt(r6) + " " + Ashes.Text.fromInt(r7))
