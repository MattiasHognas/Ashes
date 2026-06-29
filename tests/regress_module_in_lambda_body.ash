// expect: 1:3
// A shipped-helper module (Ashes.String) called from inside lambda bodies. Before
// the FreeVars capture fix these failed with a spurious "Unknown module" because
// the stitched binding was never captured into the closure.
import Ashes.String
import Ashes.IO
import Ashes.Text
let firstB s = Ashes.String.indexOf(s)("b")
in 
    let rec countUntilSemi text n = 
        match Ashes.Text.uncons(text) with
            | None -> n
            | Some((head, tail)) -> 
                if head == ";"
                then n
                else countUntilSemi(tail)(n + 1)
    in Ashes.IO.print(Ashes.Text.fromInt(firstB("abc")) + ":" + Ashes.Text.fromInt(countUntilSemi("abc;d")(0)))
