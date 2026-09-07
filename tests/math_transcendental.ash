// expect: 0 1 1 1 1024 3 3 3 5 1 3
import Ashes.IO as io
import Ashes.Number.Math as math
let intToStr n = Ashes.Text.fromInt(n)

let space = " "

io.print(intToStr(0.0
|> math.sin
|> math.roundToInt) + space + intToStr(0.0
|> math.cos
|> math.roundToInt) + space + intToStr(0.0
|> math.exp
|> math.roundToInt) + space + intToStr(math.e
|> math.ln
|> math.roundToInt) + space + intToStr(10.0
|> math.powF(2.0)
|> math.roundToInt) + space + intToStr(8.0
|> math.log2
|> math.roundToInt) + space + intToStr(1000.0
|> math.log10
|> math.roundToInt) + space + intToStr(27.0
|> math.cbrt
|> math.roundToInt) + space + intToStr(4.0
|> math.hypot(3.0)
|> math.roundToInt) + space + intToStr(3.0
|> math.fmod(10.0)
|> math.roundToInt) + space + intToStr(math.roundToInt(math.atan2(1.0)(1.0) * 4.0)))
