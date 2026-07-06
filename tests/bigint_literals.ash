// expect: 123 999999999999999999999999999999 1000000000000000000000000 121932631112635269
import Ashes.IO as io
import Ashes.BigInt as big
let a = 123N

let b = 999999999999999999999999999999N

let c = 1000000000000N * 1000000000000N

let d = 123456789N * 987654321N

io.print(Ashes.Text.fromBigInt(a) + " " + Ashes.Text.fromBigInt(b) + " " + Ashes.Text.fromBigInt(c) + " " + Ashes.Text.fromBigInt(d))
