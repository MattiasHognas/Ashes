// expect: 0 1 1 1 1024 3 3 3 5 1 3
import Ashes.IO as io
import Ashes.Math as math
let intToStr n = Ashes.Text.fromInt(n)

let space = " "

io.print(intToStr(math.roundToInt(math.sin(0.0))) + space + intToStr(math.roundToInt(math.cos(0.0))) + space + intToStr(math.roundToInt(math.exp(0.0))) + space + intToStr(math.roundToInt(math.ln(math.e))) + space + intToStr(math.roundToInt(math.powF(2.0)(10.0))) + space + intToStr(math.roundToInt(math.log2(8.0))) + space + intToStr(math.roundToInt(math.log10(1000.0))) + space + intToStr(math.roundToInt(math.cbrt(27.0))) + space + intToStr(math.roundToInt(math.hypot(3.0)(4.0))) + space + intToStr(math.roundToInt(math.fmod(10.0)(3.0))) + space + intToStr(math.roundToInt(math.atan2(1.0)(1.0) * 4.0)))
