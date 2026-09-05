// A runtime-managed string loop parameter consed into a sibling accumulator at the tail self-call
// is retained for the cell: the back edge releases the parameter's old reference when its own
// argument rebuilds the string, and without the retain the accumulated list read freed strings.
// The churn loop reuses the freed cells so a missing retain shows up as a wrong total or a crash.
// expect: 17502612|3000|17505612
import Ashes.Collection.List
let recursive collect (n: Int) (text: Str) (acc: List(Str)) =
    if n == 0
    then acc
    else collect(n - 1)(text + Ashes.Text.fromInt(n))(text :: acc)

let recursive totalLength (xs: List(Str)) (n: Int) =
    match xs with
        | [] -> n
        | text :: rest -> totalLength(rest)(n + Ashes.Text.byteLength(text))

let items = collect(3000)("seed")([])

let churn = collect(3000)("other")([])

Ashes.IO.print(Ashes.Text.fromInt(totalLength(items)(0)) + "|" + Ashes.Text.fromInt(Ashes.Collection.List.length(items)) + "|" + Ashes.Text.fromInt(totalLength(churn)(0)))
