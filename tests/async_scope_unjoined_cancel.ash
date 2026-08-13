// expect: done
let program =
    async(match await Ashes.Task.fork(Ashes.Task.sleep(600000)) with
        | Error(_) -> "fork-error"
        | Ok(_) -> "done")
in
    Ashes.IO.print(match Ashes.Task.run(program) with
        | Error(_) -> "scope-error"
        | Ok(value) -> value)
