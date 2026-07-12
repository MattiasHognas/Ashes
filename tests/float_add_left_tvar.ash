// Regression: `+` resolved a type-variable LEFT operand against a concrete RIGHT operand, but its
// resolution switch was missing the Float case (it had Str/BigInt/UInt), so `acc + x * 2.0` with an
// unresolved accumulator `acc` and a Float right operand defaulted `acc` to Int -> ASH002. (Subtract
// / Multiply use the symmetric ResolveNumericOperandTypes and were unaffected; only `+` with the Float
// on the right.) n-body and spectral-norm needed operand-reordering before this.
// expect: 7.0 6.0 3.0
import Ashes.IO as io
import Ashes.Text as text
let calc px h = px + h * 2.0

let recursive foo xs px =
    match xs with
        | [] -> px
        | h :: rest -> foo(rest)(px + h * 2.0)

let recursive mac bodies px =
    match bodies with
        | [] -> px
        | vx :: rest -> mac(rest)(px + vx * 0.5)

io.print(text.formatFloat(calc(1.0)(3.0))(1) + " " + text.formatFloat(foo([3.0])(0.0))(1) + " " + text.formatFloat(mac([2.0, 4.0])(0.0))(1))
