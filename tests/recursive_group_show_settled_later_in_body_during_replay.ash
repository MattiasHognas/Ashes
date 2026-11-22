// expect: 12
let describe (value: Str) = value + "!"

let recursive count label values =
    if Ashes.Text.length(Ashes.Trait.Show.show(label)) > 10
    then 0
    else
        match values with
            | [] -> 0
            | _value :: rest ->
                step(count(label))(rest) + Ashes.Text.length(describe(label))
and step recur rest = 1 + recur(rest)

let ordered =
    ((given (left) ->
        given (right) -> left >= right))("b")("a")

Ashes.IO.print(if ordered
then count("ab")([5, 6, 7])
else 0)
