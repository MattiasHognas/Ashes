// expect: outer-done
let inner =
    async(match await Ashes.Task.fork(Ashes.Task.sleep(600000)) with
        | Error(_) -> "inner-error"
        | Ok(_) -> "inner-done")

let program =
    async(match await Ashes.Task.fork(inner) with
        | Error(_) -> "outer-error"
        | Ok(_) -> "outer-done")
in
    Ashes.IO.print(match Ashes.Task.run(program) with
        | Error(_) -> "run-error"
        | Ok(value) -> value)
