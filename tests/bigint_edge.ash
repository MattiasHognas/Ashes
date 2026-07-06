// expect: 0 0 -5 42 -42 265252859812191058636308480000000 0 -3 -2 -1
import Ashes.IO as io
import Ashes.BigInt as big
let sp = " "

let recursive fact n acc = 
    if n == 0
    then acc
    else fact(n - 1)(big.mul(acc)(big.fromInt(n)))

let z = Ashes.Text.fromBigInt(big.fromInt(0))

let mz = Ashes.Text.fromBigInt(big.mul(big.fromInt(0))(big.fromInt(123)))

let az = Ashes.Text.fromBigInt(big.add(big.fromInt(0))(big.fromInt(-5)))

let nn = Ashes.Text.fromBigInt(big.mul(big.fromInt(-6))(big.fromInt(-7)))

let np = Ashes.Text.fromBigInt(big.mul(big.fromInt(-6))(big.fromInt(7)))

let f30 = Ashes.Text.fromBigInt(fact(30)(big.fromInt(1)))

let sz = Ashes.Text.fromBigInt(big.sub(big.fromInt(100))(big.fromInt(100)))

let ndiv = Ashes.Text.fromBigInt(big.div(big.fromInt(-17))(big.fromInt(5)))

let nmod = Ashes.Text.fromBigInt(big.mod(big.fromInt(-17))(big.fromInt(5)))

let cmp = Ashes.Text.fromInt(big.compare(big.fromInt(-5))(big.fromInt(-3)))

io.print(z + sp + mz + sp + az + sp + nn + sp + np + sp + f30 + sp + sz + sp + ndiv + sp + nmod + sp + cmp)
