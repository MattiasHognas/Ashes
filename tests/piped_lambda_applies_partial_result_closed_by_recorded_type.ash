// expect: b-3,b-2,b-1|3|alpha!
import Ashes.Text
import Ashes.IO
import Ashes.Collection.List
// A pipeline stage `given (f) -> f(x)` applies the partial application it receives. Lowering meets
// the call before anything in the stage fixes its result type, so the result layout is open there,
// while the type checker recorded it closed (a list of text, an integer, a record): the call takes
// its result by the callee's returns bit instead of asking for an arena result.
type Labelled =
    | label: Str
    | count: Int

let recursive labels (prefix: Str) (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else labels(prefix)(n - 1)(prefix + "-" + Ashes.Text.fromInt(n) :: acc)

let shout (text: Str) (count: Int) = Labelled(label = text + "!", count = count)

let joined =
    "b"
    |> labels
    |> (given (f) -> f(3)([]))
    |> Ashes.Collection.List.reverse
    |> Ashes.Text.join(",")

let counted =
    "b"
    |> labels
    |> (given (f) -> f(3)([]))
    |> Ashes.Collection.List.length

let shouted =
    "alpha"
    |> shout
    |> (given (f) -> f(1))
in Ashes.IO.print(joined + "|" + Ashes.Text.fromInt(counted) + "|" + shouted.label)
