// mandelbrot -- Benchmarks Game challenge.
//
// For an N*N region of the complex plane, iterate z = z^2 + c up to 50 times per pixel and test
// escape (|z|^2 > 4). Emits the reference binary PBM (P4): a "P4\n<N> <N>\n" header followed by one
// in/out bit per pixel, packed 8 pixels per byte (MSB = leftmost pixel), each row padded to a byte.
// The pure-Float escape loop (only + - * and a > 4.0 test, no transcendentals) is the benchmark's
// work; its accumulators are scalar Int/Float, so the compute is constant-memory. The packed image
// is built as a byte list and written with Ashes.IO.writeBytes (raw stdout).
//
// Usage: ./mandelbrot 1000   (defaults to 200)
import Ashes.IO as io
import Ashes.Text as text
import Ashes.Math as math
import Ashes.Bytes as bytes
import Ashes.UInt as uint
import Ashes.List as list
let recursive pow2 k = 
    if k == 0
    then 1
    else 2 * pow2(k - 1)

let recursive escape : Float -> Float -> Float -> Float -> Int -> Int = 
    given (zr) -> 
        given (zi) -> 
            given (cr) -> 
                given (ci) -> 
                    given (iter) -> 
                        if iter == 0
                        then 1
                        else 
                            let zr2 = zr * zr
                            in 
                                let zi2 = zi * zi
                                in 
                                    if zr2 + zi2 > 4.0
                                    then 0
                                    else escape(cr + zr2 - zi2)(2.0 * zr * zi + ci)(cr)(ci)(iter - 1)

let pixel px py n = 
    (let cr = 2.0 * math.toFloat(px) / math.toFloat(n) - 1.5
    in 
        let ci = 2.0 * math.toFloat(py) / math.toFloat(n) - 1.0
        in escape(0.0)(0.0)(cr)(ci)(50))

let recursive pack px py curByte bits n acc = 
    if py == n
    then acc
    else 
        if px == n
        then 
            if bits == 0
            then pack(0)(py + 1)(0)(0)(n)(acc)
            else pack(0)(py + 1)(0)(0)(n)(uint.fromInt(curByte * pow2(8 - bits)) :: acc)
        else 
            let curByte2 = curByte * 2 + pixel(px)(py)(n)
            in 
                if bits + 1 == 8
                then pack(px + 1)(py)(0)(0)(n)(uint.fromInt(curByte2) :: acc)
                else pack(px + 1)(py)(curByte2)(bits + 1)(n)(acc)

let render n = 
    (let header = "P4\n" + text.fromInt(n) + " " + text.fromInt(n) + "\n"
    in 
        let image = bytes.fromList(list.reverse(pack(0)(0)(0)(0)(n)([])))
        in 
            let _ = io.write(header)
            in io.writeBytes(image))

match io.args with
    | arg :: _ -> 
        match text.parseInt(arg) with
            | Ok(n) -> render(n)
            | Error(_) -> render(200)
    | [] -> render(200)
