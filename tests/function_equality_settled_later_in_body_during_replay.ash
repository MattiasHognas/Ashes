// expect: 2
let total (xs: List(Int)) = Ashes.Collection.List.length(xs)

let sizeOf xs =
    if xs == []
    then 0
    else total(xs)

let ordered =
    ((given (left) ->
        given (right) -> left >= right))(2)(1)

Ashes.IO.print(if ordered
then sizeOf([1, 2])
else 0)
