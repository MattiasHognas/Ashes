// expect: ACGT|NACGT
import Ashes.Rune as rune
import Ashes.Text as text
let recursive copyRunes input output =
    match text.uncons(input) with
        | None -> output
        | Some((head, tail)) -> copyRunes(tail)(output + rune.toText(head))

let complement value =
    match value with
        | 'A' -> 'T'
        | 'C' -> 'G'
        | 'G' -> 'C'
        | 'T' -> 'A'
        | other -> other

let recursive reverseComplement input output =
    match text.uncons(input) with
        | None -> output
        | Some((head, tail)) -> reverseComplement(tail)(rune.toText(complement(head)) + output)

Ashes.IO.print(copyRunes("ACGT")("") + "|" + reverseComplement("ACGTN")(""))
