// expect-compile-error: ASH008
async(match await Ashes.Task.fork(async 1) with
    | Error(_) -> 0
    | Ok(joiner) ->
        let first = Ashes.Task.join(joiner)
        in
            let second = Ashes.Task.join(joiner)
            in 0)
