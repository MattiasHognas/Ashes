// expect-compile-error: ASH002 Type mismatch: Bool vs Int.
let job n =
    async(let recursive helper : Int -> Bool =
        given (k) ->
            match await Ashes.Task.sleep(k) with
                | Ok(_tick) -> 1
                | Error(_message) -> 0
    in helper(n))

job
