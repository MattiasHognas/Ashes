// spectral-norm -- Benchmarks Game challenge.
//
// Compute the spectral norm of the infinite matrix A(i,j) = 1/((i+j)(i+j+1)/2 + i + 1)
// via 10 iterations of the power method (u -> AtA*u), then print
// sqrt(uT*(AtA*u) / uT*u) to 9 decimal places. The vector is an immutable List(Float)
// threaded through the recursion; the matrix entry is computed on the fly.
//
// Usage: ./spectral-norm 100   (defaults to 100)
import Ashes.IO as io
import Ashes.Text as text
import Ashes.Number.Math as math
let a i j = 1.0 / math.toFloat((i + j) * (i + j + 1) / 2 + i + 1)

let recursive ones i acc =
    if i == 0
    then acc
    else ones(i - 1)(1.0 :: acc)

let recursive avRow i j v acc =
    match v with
        | [] -> acc
        | x :: rest -> avRow(i)(j + 1)(rest)(a(i)(j) * x + acc)

let recursive atRow i j v acc =
    match v with
        | [] -> acc
        | x :: rest -> atRow(i)(j + 1)(rest)(a(j)(i) * x + acc)

let recursive mulAv i v acc =
    if i < 0
    then acc
    else mulAv(i - 1)(v)(avRow(i)(0)(v)(0.0) :: acc)

let recursive mulAtv i v acc =
    if i < 0
    then acc
    else mulAtv(i - 1)(v)(atRow(i)(0)(v)(0.0) :: acc)

let mulAtAv u n = mulAtv(n - 1)(mulAv(n - 1)(u)([]))([])

let recursive dot2 : List(Float) -> List(Float) -> Float -> Float =
    given (xs) ->
        given (ys) ->
            given (acc) ->
                match xs with
                    | [] -> acc
                    | x :: xr ->
                        match ys with
                            | [] -> acc
                            | y :: yr -> dot2(xr)(yr)(x * y + acc)

let recursive powerLoop k u v n =
    if k == 0
    then math.sqrt(dot2(u)(v)(0.0) / dot2(v)(v)(0.0))
    else
        let v2 = mulAtAv(u)(n)
        in
            let u2 = mulAtAv(v2)(n)
            in powerLoop(k - 1)(u2)(v2)(n)

let spectralNorm n =
    (let u0 = ones(n)([])
    in powerLoop(10)(u0)(u0)(n))

let run n = io.print(text.formatFloat(spectralNorm(n))(9))

match io.args with
    | arg :: _ ->
        match text.parseInt(arg) with
            | Ok(n) -> run(n)
            | Error(_) -> run(100)
    | [] -> run(100)
