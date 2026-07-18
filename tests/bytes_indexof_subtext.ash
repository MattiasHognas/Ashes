// expect: 7|12|-1|21|Hamburg|12.0|Bulawayo||Ha
import Ashes.Byte
import Ashes.Text
import Ashes.IO
let b = Ashes.Byte.fromText("Hamburg;12.0\nBulawayo;8.9\n")

let sep0 = Ashes.Byte.indexOf(b)(59)(0)

let nl0 = Ashes.Byte.indexOf(b)(10)(0)

let missing = Ashes.Byte.indexOf(b)(64)(0)

let sep1 = Ashes.Byte.indexOf(b)(59)(nl0 + 1)

let name0 = Ashes.Byte.subText(b)(0)(sep0)

let val0 = Ashes.Byte.subText(b)(sep0 + 1)(nl0 - sep0 - 1)

let name1 = Ashes.Byte.subText(b)(nl0 + 1)(sep1 - nl0 - 1)

let clampedEmpty = Ashes.Byte.subText(b)(999)(4)

let clampedLen = Ashes.Byte.subText(b)(0)(2)
in Ashes.IO.writeLine(Ashes.Text.fromInt(sep0) + "|" + Ashes.Text.fromInt(nl0) + "|" + Ashes.Text.fromInt(missing) + "|" + Ashes.Text.fromInt(sep1) + "|" + name0 + "|" + val0 + "|" + name1 + "|" + clampedEmpty + "|" + clampedLen)
