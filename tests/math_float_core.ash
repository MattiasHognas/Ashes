// expect: 4 3 4 3 3 3 7 10 2 3 -1
import Ashes.IO as io
import Ashes.Math as math
let intToStr n = Ashes.Text.fromInt(n)

let space = " "

io.print(intToStr(math.floorToInt(math.sqrt(math.toFloat(16)))) + space + intToStr(math.truncToInt(math.floor(3.7))) + space + intToStr(math.truncToInt(math.ceil(3.2))) + space + intToStr(math.roundToInt(2.5)) + space + intToStr(math.truncToInt(math.trunc(3.9))) + space + intToStr(math.floorToInt(3.9)) + space + intToStr(math.truncToInt(math.absF(0.0 - 7.0))) + space + intToStr(math.truncToInt(math.clampF(0.0)(10.0)(15.0))) + space + intToStr(math.truncToInt(math.minF(3.5)(2.5))) + space + intToStr(math.truncToInt(math.maxF(3.5)(2.5))) + space + intToStr(math.roundToInt(math.signumF(0.0 - 4.0))))
