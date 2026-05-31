// expect: 1.0e+20|-1.0e+20
match Ashes.Text.parseFloat("1e20") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) ->
        match Ashes.Text.parseFloat("-1e20") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(negativeValue) -> Ashes.IO.print(Ashes.Text.fromFloat(value) + "|" + Ashes.Text.fromFloat(negativeValue))
