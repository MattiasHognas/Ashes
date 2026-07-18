// expect: -1 0 1 -1 1 0 1
import Ashes.IO
import Ashes.Byte
let r1 = Ashes.Byte.compare(Ashes.Byte.fromText("abc"))(Ashes.Byte.fromText("abd"))

let r2 = Ashes.Byte.compare(Ashes.Byte.fromText("abc"))(Ashes.Byte.fromText("abc"))

let r3 = Ashes.Byte.compare(Ashes.Byte.fromText("abd"))(Ashes.Byte.fromText("abc"))

let r4 = Ashes.Byte.compare(Ashes.Byte.fromText("ab"))(Ashes.Byte.fromText("abc"))

let r5 = Ashes.Byte.compare(Ashes.Byte.fromText("abc"))(Ashes.Byte.fromText("ab"))

let r6 = Ashes.Byte.compare(Ashes.Byte.fromText(""))(Ashes.Byte.fromText(""))

let r7 = Ashes.Byte.compare(Ashes.Byte.fromText("b"))(Ashes.Byte.fromText("a"))

Ashes.IO.writeLine(Ashes.Text.fromInt(r1) + " " + Ashes.Text.fromInt(r2) + " " + Ashes.Text.fromInt(r3) + " " + Ashes.Text.fromInt(r4) + " " + Ashes.Text.fromInt(r5) + " " + Ashes.Text.fromInt(r6) + " " + Ashes.Text.fromInt(r7))
