// expect: 6 -1 3 9 5 12 84 243 12 (5, 2)
import Ashes.IO as io
import Ashes.Number.Math as math
let intToStr n = Ashes.Text.fromInt(n)

let space = " "

let dm = math.divMod(17)(3)

let dmStr =
    match dm with
        | (q, r) -> "(" + intToStr(q) + ", " + intToStr(r) + ")"

io.print(intToStr(math.abs(-6)) + space + intToStr(math.signum(-42)) + space + intToStr(math.min(3)(8)) + space + intToStr(math.max(9)(4)) + space + intToStr(math.clamp(0)(10)(5)) + space + intToStr(math.gcd(24)(36)) + space + intToStr(math.lcm(12)(21)) + space + intToStr(math.pow(3)(5)) + space + intToStr(math.isqrt(144)) + space + dmStr)
