// Regression: both operands of a generic multiply remain abstract until Multiply(a) evidence is
// supplied by the call site. The same constrained dot product works at Float and Int, while inferred
// constraints let the square helper resolve at BigInt without a provisional operator instruction.
// expect: 32.0 32 1000000000000000000000000
import Ashes.IO as io
import Ashes.Text as text
let recursive dotf xs ys acc =
    match xs with
        | [] -> acc
        | x :: xt ->
            match ys with
                | [] -> acc
                | y :: yt -> dotf(xt)(yt)(acc + x * y)

let recursive doti xs ys acc =
    match xs with
        | [] -> acc
        | x :: xt ->
            match ys with
                | [] -> acc
                | y :: yt -> doti(xt)(yt)(acc + x * y)

let sq x = x * x

io.print(text.formatFloat(dotf([1.0, 2.0, 3.0])([4.0, 5.0, 6.0])(0.0))(1) + " " + text.fromInt(doti([1, 2, 3])([4, 5, 6])(0)) + " " + text.fromBigInt(sq(1000000000000N)))
