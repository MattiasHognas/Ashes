// expect: 42
let program =
    async(match await Ashes.Task.fork(async 42) with
        | Error(_) -> 0
        | Ok(joiner) ->
            match await Ashes.Task.join(joiner) with
                | Error(_) -> 0
                | Ok(value) -> value)
in
    Ashes.IO.print(match Ashes.Task.run(program) with
        | Error(_) -> 0
        | Ok(value) -> value)
