// k-nucleotide -- Benchmarks Game challenge.
//
// Reads the >THREE sequence from a FASTA stream on stdin, then:
//   - counts all 1- and 2-nucleotide frequencies, printed sorted by descending
//     frequency as percentages to three decimal places;
//   - counts occurrences of specific k-mers (GGT, GGTA, GGTATT, GGTATTTTAATT,
//     GGTATTTTAATTTATAGT).
//
// It is a hash-map throughput benchmark over a very large number of short keys.
//
// The window keys are cut with Ashes.Byte.subText over a byte buffer (O(k) per
// slice, byte-indexed). The natural spelling with Ashes.Text.substring is
// catastrophically slow -- its character index makes a sliding window superlinear
// (a 5000-base sequence took ~2 minutes) -- so the buffer is materialized once
// with Ashes.Byte.fromText and sliced by byte offset.
//
// Usage: ./fasta 1000 | ./k-nucleotide
import Ashes.IO as io
import Ashes.Text as text
import Ashes.Byte as bytes
import Ashes.Collection.HashMap as map
import Ashes.Collection.List as list
import Ashes.Number.Math as math
let recursive collect started acc =
    match io.readLine(Unit) with
        | None -> acc
        | Some(line) ->
            if text.startsWith(line)(">")
            then
                if started
                then acc
                else collect(text.startsWith(line)(">THREE"))(acc)
            else
                if started
                then collect(true)(line :: acc)
                else collect(false)(acc)

let recursive countKmers buf len k i m =
    if i + k > len
    then m
    else
        let key = bytes.subText(buf)(i)(k)
        in
            let cur =
                match map.get(key)(m) with
                    | None -> 0
                    | Some(c) -> c
            in countKmers(buf)(len)(k)(i + 1)(map.set(key)(cur + 1)(m))

let before a b =
    match a with
        | (ka, ca) ->
            match b with
                | (kb, cb) ->
                    if ca == cb
                    then text.compare(ka)(kb) <= -1
                    else ca > cb

let recursive merge xs ys =
    match xs with
        | [] -> ys
        | x :: xt ->
            match ys with
                | [] -> xs
                | y :: yt ->
                    if before(x)(y)
                    then x :: merge(xt)(ys)
                    else y :: merge(xs)(yt)

let recursive split xs left right =
    match xs with
        | [] -> (left, right)
        | x :: [] -> (x :: left, right)
        | x :: y :: rest -> split(rest)(x :: left)(y :: right)

let recursive msort xs =
    match xs with
        | [] -> []
        | x :: [] -> xs
        | _ ->
            match split(xs)([])([]) with
                | (l, r) -> merge(msort(l))(msort(r))

let entries m =
    map.foldLeft(given (acc) ->
        given (k) ->
            given (v) -> (k, v) :: acc)([])(m)

let recursive renderFreq pairs total out =
    match pairs with
        | [] -> out
        | (k, c) :: rest ->
            let pct = math.toFloat(c) * 100.0 / math.toFloat(total)
            in renderFreq(rest)(total)(out + k + " " + text.formatFloat(pct)(3) + "\n")

let frequencyTable buf len k =
    (let m = countKmers(buf)(len)(k)(0)(map.empty)
    in
        let total = len - k + 1
        in renderFreq(msort(entries(m)))(total)(""))

let countSpecific buf len kmer =
    (let k = text.length(kmer)
    in
        let m = countKmers(buf)(len)(k)(0)(map.empty)
        in
            match map.get(kmer)(m) with
                | None -> 0
                | Some(c) -> c)

let specificLine buf len kmer = text.fromInt(countSpecific(buf)(len)(kmer)) + "\t" + kmer + "\n"

let run seq =
    (let buf = bytes.fromText(seq)
    in
        let len = bytes.length(buf)
        in io.write(frequencyTable(buf)(len)(1) + "\n" + frequencyTable(buf)(len)(2) + "\n" + specificLine(buf)(len)("ggt") + specificLine(buf)(len)("ggta") + specificLine(buf)(len)("ggtatt") + specificLine(buf)(len)("ggtattttaatt") + specificLine(buf)(len)("ggtattttaatttatagt")))

run(text.join("")(list.reverse(collect(false)([]))))
