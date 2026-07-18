// expect: 1000000000000000000 1000000000000000000000000 -3 142857 1 -1 0
import Ashes.IO as io
import Ashes.Number.BigInt as big
let sp = " "

let a = big.fromInt(999999999999999999)

let one = big.fromInt(1)

let sum = big.add(a)(one)

let m = big.mul(big.fromInt(1000000000000))(big.fromInt(1000000000000))

let neg = big.sub(big.fromInt(5))(big.fromInt(8))

let q = big.div(big.fromInt(1000000))(big.fromInt(7))

let r = big.mod(big.fromInt(1000000))(big.fromInt(7))

let c1 = big.compare(big.fromInt(3))(big.fromInt(9))

let c2 = big.compare(big.fromInt(4))(big.fromInt(4))

io.print(Ashes.Text.fromBigInt(sum) + sp + Ashes.Text.fromBigInt(m) + sp + Ashes.Text.fromBigInt(neg) + sp + Ashes.Text.fromBigInt(q) + sp + Ashes.Text.fromBigInt(r) + sp + Ashes.Text.fromInt(c1) + sp + Ashes.Text.fromInt(c2))
