// expect: 4 3 4 3 3 3 7 10 2 3 -1
import Ashes.IO as io
import Ashes.Number.Math as math
let intToStr n = Ashes.Text.fromInt(n)

let space = " "

io.print(intToStr(16
|> math.toFloat
|> math.sqrt
|> math.floorToInt) + space + intToStr(3.7
|> math.floor
|> math.truncToInt) + space + intToStr(3.2
|> math.ceil
|> math.truncToInt) + space + intToStr(math.roundToInt(2.5)) + space + intToStr(3.9
|> math.trunc
|> math.truncToInt) + space + intToStr(math.floorToInt(3.9)) + space + intToStr(0.0 - 7.0
|> math.absF
|> math.truncToInt) + space + intToStr(15.0
|> math.clampF(0.0)(10.0)
|> math.truncToInt) + space + intToStr(2.5
|> math.minF(3.5)
|> math.truncToInt) + space + intToStr(2.5
|> math.maxF(3.5)
|> math.truncToInt) + space + intToStr(0.0 - 4.0
|> math.signumF
|> math.roundToInt))
