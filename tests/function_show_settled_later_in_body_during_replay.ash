// expect: 7
let describe (value: Str) = value + "!"

let label x =
    Ashes.Text.length(Ashes.Trait.Show.show(x)) + Ashes.Text.length(describe(x))

let ordered =
    ((given (left) ->
        given (right) -> left >= right))(2)(1)

Ashes.IO.print(if ordered
then label("ab")
else 0)
