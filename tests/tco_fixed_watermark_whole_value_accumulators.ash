// Regression: a TCO loop threading only non-sharing whole-value accumulators (String / BigInt, no
// cons-lists) resets to a FIXED loop-entry watermark, reclaiming the previous iteration's whole-value
// copy instead of stranding it below an advancing watermark. That turns a growing String/BigInt
// accumulator from O(N^2) resident memory to O(N) (pidigits N=1000 dropped from 168 MB to a constant
// 0.25 MB). This test pins CORRECTNESS of that multi-accumulator copy-out to the fixed mark: a String
// accumulator, a BigInt power accumulator and an Int sum are threaded together and the exact results
// are checked. (A cons-list threaded alongside would disqualify the fixed mark and keep the advancing
// one; covered by tco_multi_fresh_list_accumulator.ash.)
// expect: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1099511627776 780
import Ashes.IO as io
import Ashes.Text as text
import Ashes.BigInt as big
let recursive go i n s pow acc = 
    if i == n
    then s + " " + text.fromBigInt(pow) + " " + text.fromInt(acc)
    else go(i + 1)(n)(s + "a")(pow * 2N)(acc + i)

io.print(go(0)(40)("")(1N)(0))
