// mandelbrot -- Benchmarks Game challenge (compute core).
//
// For an N*N region of the complex plane, iterate z = z^2 + c up to 50 times per pixel and test
// escape (|z|^2 > 4). The reference packs one in/out bit per pixel into a binary PBM (P4) on stdout;
// Ashes.IO has no raw-bytes stdout write (write takes a UTF-8 Str), so this reports the count of
// in-set pixels as a checksum instead. The benchmark's actual work -- the N*N * up-to-50 pure-Float
// escape loop (only + - * and a > 4.0 test, no transcendentals) -- is unchanged. Accumulators are
// scalar Int/Float, so there is no pointer-bearing arena churn here.
//
// Usage: ./mandelbrot 1000   (defaults to 200)
import Ashes.IO as io
import Ashes.Text as text
import Ashes.Math as math
let recursive escape : Float -> Float -> Float -> Float -> Int -> Int = 
    given (zr) -> 
        given (zi) -> 
            given (cr) -> 
                given (ci) -> 
                    given (iter) -> 
                        if iter == 0
                        then 1
                        else 
                            let zr2 = 1.0 * zr * zr
                            in 
                                let zi2 = 1.0 * zi * zi
                                in 
                                    if zr2 + zi2 > 4.0
                                    then 0
                                    else escape(zr2 - zi2 + cr)(2.0 * zr * zi + ci)(cr)(ci)(iter - 1)

let recursive rowLoop px py n count = 
    if px == n
    then count
    else 
        let cr = 2.0 * math.toFloat(px) / math.toFloat(n) - 1.5
        in 
            let ci = 2.0 * math.toFloat(py) / math.toFloat(n) - 1.0
            in rowLoop(px + 1)(py)(n)(count + escape(0.0)(0.0)(cr)(ci)(50))

let recursive colLoop py n count = 
    if py == n
    then count
    else colLoop(py + 1)(n)(rowLoop(0)(py)(n)(count))

let mandelbrot n = "in-set pixels: " + text.fromInt(colLoop(0)(n)(0)) + " of " + text.fromInt(n * n) + "\n"

match io.args with
    | arg :: _ -> 
        match text.parseInt(arg) with
            | Ok(n) -> io.write(mandelbrot(n))
            | Error(_) -> io.write(mandelbrot(200))
    | [] -> io.write(mandelbrot(200))
