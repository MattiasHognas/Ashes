// expect: parameter b ordered
let recursive textExists name names =
    match names with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else textExists(name)(rest)

let describe parameters name =
    if textExists(name)(parameters)
    then "parameter " + name
    else "other " + name

let ordered =
    ((given (left) ->
        given (right) -> left >= right))("b")("a")

Ashes.IO.print(describe(["a", "b"])("b") + (if ordered
then " ordered"
else " unordered"))
