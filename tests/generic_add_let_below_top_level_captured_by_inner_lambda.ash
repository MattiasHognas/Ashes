// expect: 12 3.5
let describe n =
    (let double x = x + x
    in
        let whole =
            given (unit) ->
                n
                |> double
                |> Ashes.Text.fromInt
        in
            let fraction =
                given (unit) ->
                    1.75
                    |> double
                    |> Ashes.Text.fromFloat
            in whole(Unit) + " " + fraction(Unit))

6
|> describe
|> Ashes.IO.print
