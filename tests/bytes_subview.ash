// expect: cde cde 1 xyz  0
import Ashes.IO
import Ashes.Byte
import Ashes.Text
let bytes = Ashes.Byte.fromText("abcdexyz")

let view = Ashes.Byte.subView(bytes)(2)(3)

let copy = Ashes.Byte.subText(bytes)(2)(3)

let eq =
    if view == copy
    then 1
    else 0

let clamped = Ashes.Byte.subView(bytes)(5)(99)

let past = Ashes.Byte.subView(bytes)(99)(3)

let lenPast = Ashes.Text.byteLength(past)

Ashes.IO.writeLine(view + " " + copy + " " + Ashes.Text.fromInt(eq) + " " + clamped + " " + past + " " + Ashes.Text.fromInt(lenPast))
