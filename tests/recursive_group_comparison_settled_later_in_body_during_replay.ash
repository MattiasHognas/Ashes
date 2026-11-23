// expect: 3
let lower (value: Int) = value - 1

let recursive count limit floor values =
    if limit < floor
    then 0
    else
        match values with
            | [] -> 0
            | _value :: rest ->
                step(count(limit)(floor))(rest) + count(lower(limit))(lower(floor))([])
and step recur rest = 1 + recur(rest)

let ordered =
    ((given (left) ->
        given (right) -> left >= right))("b")("a")

Ashes.IO.print(if ordered
then count(3)(1)([5, 6, 7])
else 0)
