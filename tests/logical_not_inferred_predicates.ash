// expect: true|true|1
let identity value = value

let invert value = !identity(value)

let recursive even n =
    if n == 0
    then true
    else !odd(n - 1)
and odd n =
    if n == 0
    then true
    else !even(n - 1)

let recursive retained (values: List(Int)) (rejected: List(Int)) =
    match values with
        | [] -> []
        | value :: rest ->
            if !contains(value)(rejected)
            then value :: retained(rest)(rejected)
            else retained(rest)(rejected)
and contains (value: Int) (values: List(Int)) =
    match values with
        | [] -> false
        | candidate :: rest -> candidate == value || contains(value)(rest)

let render value =
    if value
    then "true"
    else "false"

Ashes.IO.print(render(invert(false)) + "|" + render(even(10)) + "|" + Ashes.Text.fromInt([2]
|> retained([1, 2])
|> Ashes.Collection.List.length))
