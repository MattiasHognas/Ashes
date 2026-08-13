// expect: boom
let child = Ashes.Task.fromResult(Error("boom"))

let program =
    async(match await Ashes.Task.fork(child) with
        | Error(error) -> error
        | Ok(_) -> "parent-ok")
in
    Ashes.IO.print(match Ashes.Task.run(program) with
        | Error(error) -> error
        | Ok(value) -> value)
