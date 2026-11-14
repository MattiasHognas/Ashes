// expect: parameter b, other z
let recursive textExists name names =
    match names with
        | [] -> false
        | head :: rest ->
            if head == name
            then true
            else textExists(name)(rest)

let describe parameters name =
    if textExists(name)(parameters)
    then "parameter " + name
    else "other " + name

Ashes.IO.print(describe(["a", "b"])("b") + ", " + describe(["a"])("z"))
