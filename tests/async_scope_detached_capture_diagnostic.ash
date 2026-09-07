// expect-compile-error: ASH043
async(match await Ashes.Task.fork(async 1) with
    | Error(_) -> 0
    | Ok(joiner) ->
        let _ =
            joiner
            |> async
            |> Ashes.Task.spawn
        in 0)
