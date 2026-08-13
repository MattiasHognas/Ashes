// expect-compile-error: ASH043
async(match await Ashes.Task.fork(async 1) with
    | Error(_) -> 0
    | Ok(joiner) ->
        let _ = Ashes.Task.spawn(async joiner)
        in 0)
