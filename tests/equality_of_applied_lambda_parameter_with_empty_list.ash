// expect: no yes
let numbersEmpty =
    ((given (xs) -> xs == []))([1, 2])

let pairsDiffer =
    ((given (pairs) -> pairs != [(1, "a")]))([(2, "b")])

let answer flag =
    if flag
    then "yes"
    else "no"

Ashes.IO.print(answer(numbersEmpty) + " " + answer(pairsDiffer))
