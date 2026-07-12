// fasta -- Benchmarks Game challenge.
//
// Generates three DNA sequences in FASTA format: (1) the fixed ALU string tiled to 2N bases, and
// two random sequences of (2) 3N and (3) 5N bases, drawn from weighted alphabets by a specified
// deterministic LCG `seed = (seed * 3877 + 29573) mod 139968` with cumulative-probability base
// selection. All output is wrapped at 60 columns. The LCG seed is a scalar Int threaded through
// tail recursion; the random draw is a float division and the base is chosen by cumulative float
// compares -- a pure integer-arithmetic + light-float probe on the bulk-output write path.
//
// Usage: ./fasta 1000   (defaults to 1000)
import Ashes.IO as io
import Ashes.Text as text
import Ashes.Math as math
let im = 139968

let ia = 3877

let ic = 29573

let alu = "GGCCGGGCGCGGTGGCTCACGCCTGTAATCCCAGCACTTTGGGAGGCCGAGGCGGGCGGATCACGAGGTCAGGAGATCGAGACCATCCTGGCTAACACGGTGAAACCCCGTCTCTACTAAAAATACAAAAATTAGCCGGGCGTGGTGGCGCGCGCCTGTAATCCCAGCTACTCGGGAGGCTGAGGCAGGAGAATCGCTTGAACCCGGGAGGCGGAGGTTGCAGTGAGCCGAGATCGCGCCACTGCACTCCAGCCTGGGCGACAGAGCGAGACTCCGTCTCAAAAAAA"

let iub r =
    if r < 0.27
    then "a"
    else
        if r < 0.39
        then "c"
        else
            if r < 0.51
            then "g"
            else
                if r < 0.78
                then "t"
                else
                    if r < 0.80
                    then "B"
                    else
                        if r < 0.82
                        then "D"
                        else
                            if r < 0.84
                            then "H"
                            else
                                if r < 0.86
                                then "K"
                                else
                                    if r < 0.88
                                    then "M"
                                    else
                                        if r < 0.90
                                        then "N"
                                        else
                                            if r < 0.92
                                            then "R"
                                            else
                                                if r < 0.94
                                                then "S"
                                                else
                                                    if r < 0.96
                                                    then "V"
                                                    else
                                                        if r < 0.98
                                                        then "W"
                                                        else "Y"

let homo r =
    if r < 0.3029549426680
    then "a"
    else
        if r < 0.5009432431601
        then "c"
        else
            if r < 0.6984905497992
            then "g"
            else "t"

let recursive repeatFasta src remaining col out =
    if remaining == 0
    then
        if col == 0
        then out
        else out + "\n"
    else
        match text.uncons(src) with
            | None -> repeatFasta(alu)(remaining)(col)(out)
            | Some((h, t)) ->
                if col == 60
                then repeatFasta(t)(remaining - 1)(1)(out + "\n" + h)
                else repeatFasta(t)(remaining - 1)(col + 1)(out + h)

let recursive randomFasta table remaining col seed out =
    if remaining == 0
    then
        if col == 0
        then (seed, out)
        else (seed, out + "\n")
    else
        let seed2 = (seed * ia + ic) % im
        in
            let r = math.toFloat(seed2) / math.toFloat(im)
            in
                let ch = table(r)
                in
                    if col == 60
                    then randomFasta(table)(remaining - 1)(1)(seed2)(out + "\n" + ch)
                    else randomFasta(table)(remaining - 1)(col + 1)(seed2)(out + ch)

let fasta n =
    (let one = ">ONE Homo sapiens alu\n" + repeatFasta(alu)(2 * n)(0)("")
    in
        match randomFasta(iub)(3 * n)(0)(42)("") with
            | (seed2, two) ->
                match randomFasta(homo)(5 * n)(0)(seed2)("") with
                    | (_, three) ->
                        let _ = io.write(one)
                        in
                            let _ = io.write(">TWO IUB ambiguity codes\n" + two)
                            in io.write(">THREE Homo sapiens frequency\n" + three))

match io.args with
    | arg :: _ ->
        match text.parseInt(arg) with
            | Ok(n) -> fasta(n)
            | Error(_) -> fasta(1000)
    | [] -> fasta(1000)
