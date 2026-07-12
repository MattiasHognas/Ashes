// reverse-complement -- Benchmarks Game challenge.
//
// Reads FASTA-format DNA from stdin. For each sequence, reverse it and map each base to its
// Watson-Crick complement (A<->T, G<->C, plus the IUPAC ambiguity codes), then write the
// complemented sequence back out wrapped at 60 columns, preserving the '>' header lines.
//
// Natural spelling: consume each sequence line with uncons, complementing every base and
// prepending it to a list -- which complements AND reverses in one linear pass. Header lines
// flush the pending reverse-complement and pass through unchanged.
import Ashes.IO as io
import Ashes.Text as text
let complement c = 
    match c with
        | "A" -> "T"
        | "C" -> "G"
        | "G" -> "C"
        | "T" -> "A"
        | "U" -> "A"
        | "M" -> "K"
        | "R" -> "Y"
        | "W" -> "W"
        | "S" -> "S"
        | "Y" -> "R"
        | "K" -> "M"
        | "V" -> "B"
        | "H" -> "D"
        | "D" -> "H"
        | "B" -> "V"
        | "N" -> "N"
        | other -> other

let recursive compLine line acc = 
    match text.uncons(line) with
        | None -> acc
        | Some((c, rest)) -> compLine(rest)(complement(c) :: acc)

let recursive emit chars col buf = 
    match chars with
        | [] -> 
            if col == 0
            then Unit
            else io.write(buf + "\n")
        | c :: rest -> 
            if col == 60
            then 
                let _ = io.write(buf + "\n")
                in emit(chars)(0)("")
            else emit(rest)(col + 1)(buf + c)

let recursive loop revcomp = 
    match io.readLine(Unit) with
        | None -> emit(revcomp)(0)("")
        | Some(line) -> 
            match text.uncons(line) with
                | Some((">", _)) -> 
                    let _ = emit(revcomp)(0)("")
                    in 
                        let _ = io.writeLine(line)
                        in loop([])
                | _ -> loop(compLine(line)(revcomp))
in loop([])
