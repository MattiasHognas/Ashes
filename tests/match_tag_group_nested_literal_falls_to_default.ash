// expect: header,seq,seq,header,seq,
import Ashes.Text as text
let classify line =
    match text.uncons(line) with
        | Some(('>', _)) -> "header"
        | _ -> "seq"

let recursive walk lines acc =
    match lines with
        | [] -> acc
        | line :: rest -> walk(rest)(acc + classify(line) + ",")

Ashes.IO.print(walk([">ONE", "ACGT", "TTGA", ">TWO", "GGCC"])(""))
