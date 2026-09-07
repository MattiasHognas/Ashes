// expect: 0 0 -5 42 -42 265252859812191058636308480000000 0 -3 -2 -1
import Ashes.IO as io
import Ashes.Number.BigInt as big
let sp = " "

let recursive fact n acc =
    if n == 0
    then acc
    else
        n
        |> big.fromInt
        |> big.mul(acc)
        |> fact(n - 1)

let z =
    0
    |> big.fromInt
    |> Ashes.Text.fromBigInt

let mz =
    123
    |> big.fromInt
    |> big.mul(big.fromInt(0))
    |> Ashes.Text.fromBigInt

let az =
    -5
    |> big.fromInt
    |> big.add(big.fromInt(0))
    |> Ashes.Text.fromBigInt

let nn =
    -7
    |> big.fromInt
    |> big.mul(big.fromInt(-6))
    |> Ashes.Text.fromBigInt

let np =
    7
    |> big.fromInt
    |> big.mul(big.fromInt(-6))
    |> Ashes.Text.fromBigInt

let f30 =
    1
    |> big.fromInt
    |> fact(30)
    |> Ashes.Text.fromBigInt

let sz =
    100
    |> big.fromInt
    |> big.sub(big.fromInt(100))
    |> Ashes.Text.fromBigInt

let ndiv =
    5
    |> big.fromInt
    |> big.div(big.fromInt(-17))
    |> Ashes.Text.fromBigInt

let nmod =
    5
    |> big.fromInt
    |> big.mod(big.fromInt(-17))
    |> Ashes.Text.fromBigInt

let cmp =
    -3
    |> big.fromInt
    |> big.compare(big.fromInt(-5))
    |> Ashes.Text.fromInt

io.print(z + sp + mz + sp + az + sp + nn + sp + np + sp + f30 + sp + sz + sp + ndiv + sp + nmod + sp + cmp)
