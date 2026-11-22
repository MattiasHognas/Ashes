// expect: 3
let recursive hide names name =
    match names with
        | [] -> []
        | other :: rest ->
            if other != name
            then other :: hide(rest)(name)
            else hide(rest)(name)

let recursive count names values =
    if names == []
    then 0
    else
        match values with
            | [] -> 0
            | _value :: rest ->
                step(count(names))(rest) + count(hide(names)("x"))([])
and step recur rest = 1 + recur(rest)

let ordered =
    ((given (left) ->
        given (right) -> left >= right))("b")("a")

Ashes.IO.print(if ordered
then count(["x", "y"])([5, 6, 7])
else 0)
