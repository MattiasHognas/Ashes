import Ashes.Bytes as bytes
import Ashes.UInt as uint
let esc = bytes.subText(bytes.appendByte(bytes.empty(Unit))(uint.fromInt(27)))(0)(1)

let reset = esc + "[0m"

let paint code s = esc + "[" + code + "m" + s + reset

let red s = paint("1;31")(s)

let green s = paint("1;32")(s)

let yellow s = paint("1;33")(s)

let blue s = paint("1;34")(s)

let magenta s = paint("1;35")(s)

let cyan s = paint("1;36")(s)

let dim s = paint("2")(s)

let clearScreen _ = esc + "[2J" + esc + "[H"

let home = esc + "[H"

let hideCursor = esc + "[?25l"

let showCursor = esc + "[?25h"

let altScreenOn = esc + "[?1049h"

let altScreenOff = esc + "[?1049l"

let mouseOn = esc + "[?1003h" + esc + "[?1006h"

let mouseOff = esc + "[?1006l" + esc + "[?1003l"
