// expect: 4|65|195|99
import Ashes.Byte
import Ashes.Text
import Ashes.IO
let b = Ashes.Byte.fromText("Aßc")
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(0)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(1)) + "|" + Ashes.Text.fromInt(Ashes.Byte.get(b)(3)))
