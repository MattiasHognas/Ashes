// Regression: a BigInt accumulator threaded through a TCO loop must survive the back-edge arena
// reset. A BigInt is a self-contained { header, limb... } buffer, so it is now copy-outed across the
// reset (like a String) -- this both keeps the value correct and lets the iteration's intermediate
// BigInt garbage be reclaimed instead of accumulating for the whole loop. Threads two BigInt
// accumulators plus Int indices and checks the exact products (30! and 2^30).
// expect: 265252859812191058636308480000000 1073741824
import Ashes.IO as io
import Ashes.Text as text
import Ashes.BigInt as big
let recursive run fac pow i n =
    if i > n
    then text.fromBigInt(fac) + " " + text.fromBigInt(pow)
    else run(fac * big.fromInt(i))(pow * 2N)(i + 1)(n)

io.print(run(1N)(1N)(1)(30))
