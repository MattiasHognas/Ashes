// pidigits — Benchmarks Game challenge, driven by native Ashes.Number.BigInt.
//
// Streams the first N decimal digits of pi with the unbounded spigot (Gibbons) algorithm, in the
// Benchmarks Game output format: ten digits per line, each line tagged with the running digit
// count. The running state (q, r, t) grows without bound, so every step is arbitrary-precision
// arithmetic -- the benchmark that was blocked until Ashes.Number.BigInt landed. With BigInt operators
// and `N` literals the spigot reads almost exactly like its mathematical definition.
//
// Usage: ./pidigits 10000   (defaults to 27 digits when no argument is given)
import Ashes.IO as io
import Ashes.Number.BigInt as big
let recursive padRight s n =
    if n == 0
    then s
    else padRight(s + " ")(n - 1)

let recursive spigot q r t k n l remaining lineBuf lineLen total output =
    if remaining == 0
    then
        if lineLen == 0
        then output
        else output + padRight(lineBuf)(10 - lineLen) + "\t:" + Ashes.Text.fromInt(total) + "\n"
    else
        if 4N * q + r - t < n * t
        then
            let lineBuf2 = lineBuf + Ashes.Text.fromBigInt(n)
            in
                let lineLen2 = lineLen + 1
                in
                    let total2 = total + 1
                    in
                        let q2 = 10N * q
                        in
                            let r2 = 10N * (r - n * t)
                            in
                                let n2 = 10N * (3N * q + r) / t - 10N * n
                                in
                                    if lineLen2 == 10
                                    then spigot(q2)(r2)(t)(k)(n2)(l)(remaining - 1)("")(0)(total2)(output + lineBuf2 + "\t:" + Ashes.Text.fromInt(total2) + "\n")
                                    else spigot(q2)(r2)(t)(k)(n2)(l)(remaining - 1)(lineBuf2)(lineLen2)(total2)(output)
        else spigot(q * k)((2N * q + r) * l)(t * l)(k + 1N)((q * (7N * k + 2N) + r * l) / (t * l))(l + 2N)(remaining)(lineBuf)(lineLen)(total)(output)

let pidigits count = spigot(1N)(0N)(1N)(1N)(3N)(3N)(count)("")(0)(0)("")

match Ashes.IO.args with
    | arg :: _ ->
        match Ashes.Text.parseInt(arg) with
            | Ok(count) -> io.print(pidigits(count))
            | Error(_) -> io.print(pidigits(27))
    | [] -> io.print(pidigits(27))
