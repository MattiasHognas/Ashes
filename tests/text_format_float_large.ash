// expect: 1.0e+20|-1.0e+20|1.0e+100|1.0e+308
match Ashes.Text.parseFloat("1e20") with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(value) ->
        match Ashes.Text.parseFloat("-1e20") with
            | Error(message) -> Ashes.IO.print(message)
            | Ok(negativeValue) ->
                match Ashes.Text.parseFloat("1e100") with
                    | Error(message) -> Ashes.IO.print(message)
                    | Ok(largeValue) ->
                        match Ashes.Text.parseFloat("1e308") with
                            | Error(message) -> Ashes.IO.print(message)
                            | Ok(veryLargeValue) -> Ashes.IO.print(Ashes.Text.fromFloat(value) + "|" + Ashes.Text.fromFloat(negativeValue) + "|" + Ashes.Text.fromFloat(largeValue) + "|" + Ashes.Text.fromFloat(veryLargeValue))
