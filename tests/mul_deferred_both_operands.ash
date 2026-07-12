// Regression: a bare `x * y` with BOTH operands still unbound type variables (a polymorphic dot
// product / generic multiply) eagerly defaulted to Int, so `dot … (acc + x * y)` at Float failed
// ASH002. `*` now mirrors `+`: when both operands are unconstrained it emits a provisional MulInt with
// the shared operand var kept monomorphic, patched to MulFloat / BigIntBinary (or a plain MulInt) once
// inference resolves the type (ResolveDeferredMuls). Each generic function is monomorphic (like `+`),
// so one is used per type: a Float dot product, an Int dot product, and a BigInt square.
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
