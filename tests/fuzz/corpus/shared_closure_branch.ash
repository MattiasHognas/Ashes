let shared = [1, 2, 3]
in
    let choose =
        given (flag: Bool) ->
            if flag
            then shared
            else shared
    in choose(true)
