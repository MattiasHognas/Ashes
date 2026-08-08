let captured = [1, 2, 3]
let choose =
    given (value: Int) ->
        if value > 0
        then Ok(captured)
        else Error("negative")

Ok(1) |?> choose
