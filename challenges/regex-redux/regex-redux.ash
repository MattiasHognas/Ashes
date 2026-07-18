// regex-redux -- Benchmarks Game challenge, driven by native Ashes.Text.Regex (PCRE2).
//
// Reads a FASTA DNA stream from stdin, strips header lines and newlines, then (a) counts
// non-overlapping matches of nine IUPAC variant patterns and prints each pattern with its count,
// and (b) applies the eleven IUB ambiguity-code substitutions in sequence, printing the original
// input length, the cleaned length, and the final substituted length. Regex compile / findAll /
// replace are exercised on real alternation and character-class patterns over the whole input.
//
// Usage: ./fasta 1000000 | ./regex-redux
import Ashes.IO as io
import Ashes.Text.Regex as regex
import Ashes.Collection.List as list
import Ashes.Text as text
import Ashes.Text as string
let compileOrDie pattern =
    match regex.compile(pattern) with
        | Ok(re) -> re
        | Error(message) -> io.panic(message)

let recursive readLoop ilen parts =
    match io.readLine(Unit) with
        | None -> (ilen, parts)
        | Some(line) ->
            let ilen2 = ilen + text.byteLength(line) + 1
            in
                if string.startsWith(line)(">")
                then readLoop(ilen2)(parts)
                else readLoop(ilen2)(line :: parts)

let recursive countAll seq patterns =
    match patterns with
        | [] -> Unit
        | pattern :: rest ->
            let re = compileOrDie(pattern)
            in
                let count = list.length(regex.findAll(re)(seq))
                in
                    let _printed = io.write(pattern + " " + text.fromInt(count) + "\n")
                    in countAll(seq)(rest)

let recursive applySubs seq subs =
    match subs with
        | [] -> seq
        | (pattern, replacement) :: rest ->
            let re = compileOrDie(pattern)
            in applySubs(regex.replace(re)(seq)(replacement))(rest)

let patterns = "agggtaaa|tttaccct" :: "[cgt]gggtaaa|tttaccc[acg]" :: "a[act]ggtaaa|tttacc[agt]t" :: "ag[act]gtaaa|tttac[agt]ct" :: "agg[act]taaa|ttta[agt]cct" :: "aggg[acg]aaa|ttt[cgt]ccct" :: "agggt[cgt]aa|tt[acg]accct" :: "agggta[cgt]a|t[acg]taccct" :: "agggtaa[cgt]|[acg]ttaccct" :: []

let subs = ("B", "(c|g|t)") :: ("D", "(a|g|t)") :: ("H", "(a|c|t)") :: ("K", "(g|t)") :: ("M", "(a|c)") :: ("N", "(a|c|g|t)") :: ("R", "(a|g)") :: ("S", "(c|g)") :: ("V", "(a|c|g)") :: ("W", "(a|t)") :: ("Y", "(c|t)") :: []

let result = readLoop(0)([])
in
    match result with
        | (ilen, parts) ->
            let seq = string.join("")(list.reverse(parts))
            in
                let clen = text.byteLength(seq)
                in
                    let _counted = countAll(seq)(patterns)
                    in
                        let final = applySubs(seq)(subs)
                        in
                            let _blank = io.write("\n")
                            in
                                let _il = io.print(ilen)
                                in
                                    let _cl = io.print(clen)
                                    in io.print(text.byteLength(final))
