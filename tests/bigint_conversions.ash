// expect: 42 fits -9223372036854775808 overflow 12345 999999999999999999999999 bad
import Ashes.IO as io
import Ashes.Number.BigInt as big
let showInt r =
    match r with
        | Ok(v) -> Ashes.Text.fromInt(v)
        | Error(_) -> "overflow"

let showBig r =
    match r with
        | Ok(v) -> Ashes.Text.fromBigInt(v)
        | Error(_) -> "bad"

let a =
    42
    |> big.fromInt
    |> big.toInt
    |> showInt

let big2 = big.fromInt(1000000000000) * big.fromInt(1000000000000)

let b =
    match big.toInt(big2) with
        | Ok(_) -> "fits"
        | Error(_) -> "overflow"

let c =
    big.mul(big.fromInt(-1))(big.fromInt(9223372036854775807)) - big.fromInt(1)
    |> big.toInt
    |> showInt

let d =
    match big.toInt(big2) with
        | Ok(_) -> "fits"
        | Error(m) -> "overflow"

let e =
    "12345"
    |> Ashes.Text.parseBigInt
    |> showBig

let f =
    "999999999999999999999999"
    |> Ashes.Text.parseBigInt
    |> showBig

let g =
    "12x"
    |> Ashes.Text.parseBigInt
    |> showBig

io.print(a + " fits " + c + " " + d + " " + e + " " + f + " " + g)
