// expect: 2,4,6,8,| 30 | 42:hi
import Ashes.Task.Parallel
import Ashes.Text
import Ashes.IO
let nums = 1 :: 2 :: 3 :: 4 :: []

let doubled =
    Ashes.Task.Parallel.map(given (x) -> x * 2)(nums)

let sumSq =
    Ashes.Task.Parallel.reduce(given (a) ->
        given (b) -> a + b)(0)(given (x) -> x * x)(nums)

let pair =
    Ashes.Task.Parallel.both(given (_u) -> 42)(given (_u) -> "hi")

let recursive showList xs =
    match xs with
        | [] -> ""
        | h :: t -> Ashes.Text.fromInt(h) + "," + showList(t)
in
    match pair with
        | (a, b) -> Ashes.IO.print(showList(doubled) + "| " + Ashes.Text.fromInt(sumSq) + " | " + Ashes.Text.fromInt(a) + ":" + b)
