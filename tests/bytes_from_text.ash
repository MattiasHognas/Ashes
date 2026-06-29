// expect: 4|65|195|99
import Ashes.Bytes
import Ashes.Text
import Ashes.IO
let b = Ashes.Bytes.fromText("Aßc")
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Bytes.length(b)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(0)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(1)) + "|" + Ashes.Text.fromInt(Ashes.Bytes.get(b)(3)))
