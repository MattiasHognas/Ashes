// expect: 5
let pick exact value =
    if !exact
    then value
    else 0

let ordered =
    ((given (left) ->
        given (right) -> left >= right))("b")("a")

Ashes.IO.print(if ordered
then pick(false)(5) + pick(true)(2)
else 0)
