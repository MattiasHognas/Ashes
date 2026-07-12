// expect: 42 fits -9223372036854775808 overflow 12345 999999999999999999999999 bad
import Ashes.IO as io
import Ashes.BigInt as big
let showInt r =
    match r with
        | Ok(v) -> Ashes.Text.fromInt(v)
        | Error(_) -> "overflow"

let showBig r =
    match r with
        | Ok(v) -> Ashes.Text.fromBigInt(v)
        | Error(_) -> "bad"

let a = showInt(big.toInt(big.fromInt(42)))

let big2 = big.fromInt(1000000000000) * big.fromInt(1000000000000)

let b =
    match big.toInt(big2) with
        | Ok(_) -> "fits"
        | Error(_) -> "overflow"

let c = showInt(big.toInt(big.mul(big.fromInt(-1))(big.fromInt(9223372036854775807)) - big.fromInt(1)))

let d =
    match big.toInt(big2) with
        | Ok(_) -> "fits"
        | Error(m) -> "overflow"

let e = showBig(Ashes.Text.parseBigInt("12345"))

let f = showBig(Ashes.Text.parseBigInt("999999999999999999999999"))

let g = showBig(Ashes.Text.parseBigInt("12x"))

io.print(a + " fits " + c + " " + d + " " + e + " " + f + " " + g)
