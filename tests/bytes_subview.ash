// expect: cde cde 1 xyz  0
import Ashes.IO
import Ashes.Bytes
import Ashes.String
let bytes = Ashes.Bytes.fromText("abcdexyz")

let view = Ashes.Bytes.subView(bytes)(2)(3)

let copy = Ashes.Bytes.subText(bytes)(2)(3)

let eq = 
    if view == copy
    then 1
    else 0

let clamped = Ashes.Bytes.subView(bytes)(5)(99)

let past = Ashes.Bytes.subView(bytes)(99)(3)

let lenPast = Ashes.Text.byteLength(past)

Ashes.IO.writeLine(view + " " + copy + " " + Ashes.Text.fromInt(eq) + " " + clamped + " " + past + " " + Ashes.Text.fromInt(lenPast))
