// expect: 1000000000000000000 1000000000000000000000000 -3 142857 1 -1 0
import Ashes.IO as io
import Ashes.Number.BigInt as big
let sp = " "

let a = big.fromInt(999999999999999999)

let one = big.fromInt(1)

let sum = big.add(a)(one)

let m =
    1000000000000
    |> big.fromInt
    |> big.mul(big.fromInt(1000000000000))

let neg =
    8
    |> big.fromInt
    |> big.sub(big.fromInt(5))

let q =
    7
    |> big.fromInt
    |> big.div(big.fromInt(1000000))

let r =
    7
    |> big.fromInt
    |> big.mod(big.fromInt(1000000))

let c1 =
    9
    |> big.fromInt
    |> big.compare(big.fromInt(3))

let c2 =
    4
    |> big.fromInt
    |> big.compare(big.fromInt(4))

io.print(Ashes.Text.fromBigInt(sum) + sp + Ashes.Text.fromBigInt(m) + sp + Ashes.Text.fromBigInt(neg) + sp + Ashes.Text.fromBigInt(q) + sp + Ashes.Text.fromBigInt(r) + sp + Ashes.Text.fromInt(c1) + sp + Ashes.Text.fromInt(c2))
