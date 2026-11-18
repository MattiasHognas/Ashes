// A nested operator-generic binding used at two types inside one lambda that reads the enclosing
// function's parameter: the parameter's type, settled by the function's later uses, reaches the
// lambda before the Add evidence for that use is chosen.
// expect: 12 3.5
let describe n =
    (let double x = x + x
    in
        let both =
            given (unit) ->
                Ashes.Text.fromInt(double(n)) + " " + Ashes.Text.fromFloat(double(1.75))
        in both(Unit))

6
|> describe
|> Ashes.IO.print
