// Two applied lambdas compare their parameter with the empty list at different element types. The
// shipped list equality's body is lowered once per dictionary; the element type each lowering
// settled on is pinned for that lowering only.
// expect: no no yes
let ints =
    ((given (xs) -> xs == []))([1, 2])

let texts =
    ((given (ys) -> ys == []))(["x"])

let differs =
    ((given (zs) -> zs != []))([true])

let word value =
    if value
    then "yes"
    else "no"

Ashes.IO.print(word(ints) + " " + word(texts) + " " + word(differs))
