// expect: 2
let below a b =
    if a < b
    then a + 1
    else 0

let ordered =
    ((given (left) ->
        given (right) -> left >= right))(2)(1)

Ashes.IO.print(if ordered
then below(1)(3)
else 0)
