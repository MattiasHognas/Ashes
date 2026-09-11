// The fusion declines — and the program still runs exactly as an ordinary map-then-fold would —
// whenever the soundness proof cannot go through: a callback that could panic (division), and a
// mapped list read a second time (an extra consumer, so the argument to foldLeft's third position is
// a Var, not a saturated call to map — not single-consumer). Both must still produce the same result
// an unfused map-then-fold would.
// expect: 170|12
import Ashes.Collection.List as list
let add a b = a + b

let divideResult =
    [1, 2, 5]
    |> list.map(given (x) -> 100 / x)
    |> list.foldLeft(add)(0)

let mapped =
    list.map(given (x) -> x + 1)([1, 2, 3])

let extraConsumerResult = list.foldLeft(add)(0)(mapped) + list.length(mapped)

Ashes.IO.print(Ashes.Text.fromInt(divideResult) + "|" + Ashes.Text.fromInt(extraConsumerResult))
