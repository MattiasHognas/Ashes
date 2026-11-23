// expect: 3
let both (left: Bool) (right: Bool) = left && right

let recursive count flag values =
    if !flag
    then 0
    else
        match values with
            | [] -> 0
            | _value :: rest ->
                step(count(flag))(rest) + count(both(flag)(false))([])
and step recur rest = 1 + recur(rest)

let ordered =
    ((given (left) ->
        given (right) -> left >= right))("b")("a")

Ashes.IO.print(if ordered
then count(true)([5, 6, 7])
else 0)
